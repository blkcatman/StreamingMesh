// Exercise the production jslib against a capacity-limited Media Source mock.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

class Events {
  constructor() { this.events = {}; }
  addEventListener(name, callback) { (this.events[name] ||= []).push(callback); }
  removeEventListener(name, callback) { this.events[name] = (this.events[name] || []).filter(x => x !== callback); }
  emit(name) { return Promise.all((this.events[name] || []).map(callback => callback())); }
}
function ranges(values) {
  return {length: values.length, start: i => values[i][0], end: i => values[i][1]};
}
function harness(capacity = 1000) {
  let source, interval;
  const downloads = [], attempts = [], errors = [];
  let blockFile, release;
  const audio = Object.assign(new Events(), {
    currentTime: 0, paused: true, seeking: false, style: {},
    setAttribute() {}, removeAttribute() {}, remove() {}, load() {},
    pause() { this.paused = true; },
    play() { this.paused = false; this.emit('playing'); return Promise.resolve(); },
  });
  class Buffer extends Events {
    constructor() { super(); this.values = []; this.updating = false; this.capacity = capacity; }
    get buffered() { return ranges(this.values); }
    appendBuffer(bytes) {
      attempts.push(bytes);
      const seconds = this.values.reduce((sum, range) => sum + range[1] - range[0], 0);
      if (bytes.file && seconds + bytes.end - bytes.start > this.capacity) {
        const error = new Error('Audio buffer full'); error.name = 'QuotaExceededError'; throw error;
      }
      this.updating = true;
      Promise.resolve().then(() => {
        if (bytes.file) {
          this.values.push([bytes.start, bytes.end]); this.values.sort((a,b) => a[0] - b[0]);
          this.values = this.values.reduce((result, range) => {
            const previous = result.at(-1);
            if (previous && previous[1] >= range[0]) previous[1] = Math.max(previous[1], range[1]);
            else result.push(range.slice());
            return result;
          }, []);
        }
        this.updating = false; this.emit('updateend');
      });
    }
    remove(start, end) {
      this.updating = true;
      Promise.resolve().then(() => {
        this.values = this.values.flatMap(range => {
          if (range[1] <= start || range[0] >= end) return [range];
          const result = [];
          if (range[0] < start) result.push([range[0], start]);
          if (range[1] > end) result.push([end, range[1]]);
          return result;
        });
        this.updating = false; this.emit('updateend');
      });
    }
  }
  class MediaSource extends Events {
    constructor() { super(); source = this; }
    static isTypeSupported() { return true; }
    addSourceBuffer() { this.buffer = new Buffer(); return this.buffer; }
  }
  Object.defineProperty(audio, 'buffered', {get: () => source?.buffer?.buffered || ranges([])});
  const entries = Array.from({length:36}, (_,index) => ({audio:'audio-'+index,
    startTicks:index*100000000, endTicks:(index+1)*100000000}));
  const context = {
    Module: {}, LibraryManager: {library:{}}, mergeInto: Object.assign, UTF8ToString: value => value,
    console: {error: (...args) => errors.push(args.join(' ')), warn() {}},
    MediaSource, URL: {createObjectURL: () => 'blob:test', revokeObjectURL() {}},
    document: Object.assign(new Events(), {createElement: () => audio, body:{appendChild() {}}}),
    window: {MediaSource, setInterval: callback => { interval = callback; return 1; }, clearInterval: () => {interval=null;}, setTimeout},
    async fetch(url) {
      if (url.endsWith('stream.stma')) return {ok:true, text: async () => entries.map(JSON.stringify).join('\n')};
      const file = url.split('/').at(-1); downloads.push(file);
      const entry = entries.find(x => x.audio === file);
      return {ok:true, async arrayBuffer() {
        if (file === blockFile) await new Promise(resolve => {release=resolve;});
        return {file: entry?.audio, start: entry?.startTicks/10000000, end: entry?.endTicks/10000000};
      }};
    },
  };
  vm.runInNewContext(fs.readFileSync('Assets/Plugins/WebGL/StreamingMeshFmp4.jslib','utf8'), context);
  const api = context.LibraryManager.library;
  const handle = api.STM_Fmp4_Create('http://test/', 'init', 'stream.stma', 'audio/mp4', 10000000);
  const player = context.Module.StreamingMeshFmp4.players[handle];
  const settle = async () => {for (let i=0;i<120;i++) await Promise.resolve();};
  const tick = async () => {interval?.(); await settle();};
  return {api,handle,player,audio,source,downloads,attempts,errors,settle,tick,entries,
    open: async () => {await source.emit('sourceopen'); await settle();},
    block: file => {blockFile=file;}, unblock: () => {blockFile=null; release?.();}};
}

(async () => {
  const bounded = harness(); await bounded.open(); await bounded.tick();
  assert.equal(bounded.downloads.filter(x => x !== 'init').length, 3, 'Paused start must not fetch the whole recording');
  assert.equal(bounded.source.buffer.buffered.end(0), 30);
  assert.equal(bounded.source.duration, 360, 'Partial download must preserve the full seekable timeline');
  bounded.audio.currentTime = 40; await bounded.tick();
  assert.ok(bounded.source.buffer.buffered.end(bounded.source.buffer.buffered.length - 1) === 70, 'Asynchronous eviction must not starve prefetch');
  bounded.api.STM_Fmp4_Seek(bounded.handle, 180); await bounded.tick(); await bounded.tick();
  assert.ok(bounded.source.buffer.buffered.start(0) >= 180, 'Seek must fetch the requested window');
  bounded.api.STM_Fmp4_Seek(bounded.handle, 0); await bounded.tick(); await bounded.tick();
  assert.equal(bounded.downloads.filter(x => x === 'audio-0').length, 2, 'Backward seek must refetch evicted audio');
  assert.ok(bounded.source.buffer.buffered.end(0) === 30, 'Backward seek must evict distant future audio');

  const adjustable = harness(); await adjustable.open(); await adjustable.tick();
  adjustable.api.STM_Fmp4_ConfigureBuffering(adjustable.handle, 1, 2); await adjustable.settle(); await adjustable.tick();
  assert.ok(adjustable.source.buffer.buffered.end(0) === 10, 'Shorter window must evict old future audio');
  const pausedDownloads = adjustable.downloads.length; await adjustable.tick();
  assert.equal(adjustable.downloads.length, pausedDownloads, 'Paused short window must stop downloading');
  adjustable.audio.currentTime = 12; await adjustable.tick();
  assert.ok(adjustable.source.buffer.buffered.start(0) >= 10, 'Configured history was not evicted');
  adjustable.api.STM_Fmp4_ConfigureBuffering(adjustable.handle, 3, 2); await adjustable.settle(); await adjustable.tick();
  assert.ok(adjustable.source.buffer.buffered.end(0) === 40, 'Larger window must resume prefetch');

  // Deliberately irregular, short and long files. Count follows exact PTS,
  // including boundary seeks; a nominal 10-second duration would select wrong files.
  const variable = harness(); variable.entries.splice(0, variable.entries.length,
    ...[0, 0.1, 0.3, 80.3, 81.3, 83.3].slice(0, -1).map((start, i) => ({
      audio: 'variable-' + i, startTicks: Math.round(start * 10000000),
      endTicks: Math.round([0.1, 0.3, 80.3, 81.3, 83.3][i] * 10000000)})));
  variable.entries.reverse(); await variable.open(); await variable.tick();
  assert.equal(variable.downloads.filter(x => x !== 'init').length, 3);
  assert.equal(variable.source.buffer.buffered.end(0), 80.3);
  variable.audio.currentTime = 0.1; await variable.tick();
  assert.equal(variable.downloads.filter(x => x !== 'init').length, 4, 'Exact end excludes consumed file');
  variable.api.STM_Fmp4_ConfigureBuffering(variable.handle, 1, 0); await variable.tick();
  assert.equal(variable.source.buffer.buffered.end(0), 0.3, 'Shrink uses actual boundary, not nominal duration');
  variable.api.STM_Fmp4_Seek(variable.handle, 80.3); await variable.tick(); await variable.tick();
  assert.equal(variable.source.buffer.buffered.end(0), 81.3, 'Seek at boundary selects the following file');
  variable.api.STM_Fmp4_ConfigureBuffering(variable.handle, 0, 0); await variable.tick();
  assert.equal(variable.player.aheadChunks, 1, 'Invalid file count must not change the window');

  const shortFiles = harness(); shortFiles.entries.splice(0, shortFiles.entries.length,
    ...[0, 1, 2, 3].map(i => ({audio:'short-'+i, startTicks:i*1000000,endTicks:(i+1)*1000000})));
  await shortFiles.open(); await shortFiles.tick();
  shortFiles.api.STM_Fmp4_ConfigureBuffering(shortFiles.handle, 1, 0); await shortFiles.tick();
  assert.equal(shortFiles.source.buffer.buffered.end(0), 0.1, 'Sub-quarter-second files must also obey the count after shrinking');

  const gap = harness(); gap.entries.splice(0, gap.entries.length,
    {audio:'gap-a',startTicks:0,endTicks:100000000},
    {audio:'gap-b',startTicks:200000000,endTicks:240000000},
    {audio:'gap-c',startTicks:270000000,endTicks:300000000});
  gap.api.STM_Fmp4_ConfigureBuffering(gap.handle, 2, 0); await gap.open(); await gap.tick();
  assert.equal(gap.downloads.filter(x => x !== 'init').length, 2);
  gap.audio.currentTime = 10; await gap.tick();
  assert.equal(gap.downloads.filter(x => x !== 'init').length, 3, 'Gap selects the next available files');

  const live = harness(); const later = live.entries.splice(2);
  live.entries.reverse(); await live.open(); await live.tick();
  assert.equal(live.source.buffer.buffered.end(0), 20);
  assert.equal(live.downloads[1], 'audio-0', 'PTS must order entries without sequence');
  live.entries.push(...later.slice(0, 2)); live.audio.currentTime = 15; await live.tick();
  assert.equal(live.source.buffer.buffered.end(0), 40, 'Growing playlist must append audio without reconnect');
  assert.equal(live.source.duration, 40, 'Live duration must grow with the playlist');

  const quota = harness(20); await quota.open(); await quota.tick();
  const blocked = quota.player.queue[0];
  assert.equal(blocked.file, 'audio-2'); assert.ok(quota.player.quotaBlocked);
  assert.equal(quota.player.seen['audio-2'], undefined, 'Failed append must not mark a segment consumed');
  assert.ok(quota.player.state >= 1, 'Quota must preserve a usable playback state');
  const blockedAttempts = quota.attempts.filter(x => x === blocked.bytes).length;
  quota.audio.currentTime = 12; await quota.tick();
  assert.equal(quota.attempts.filter(x => x === blocked.bytes).length, blockedAttempts + 1, 'Retry must keep exactly the same bytes');
  assert.ok(quota.player.seen['audio-2']); assert.equal(quota.errors.length, 0);

  const stale = harness(); await stale.open(); await stale.tick(); stale.block('audio-9');
  stale.api.STM_Fmp4_Seek(stale.handle, 90); await stale.tick();
  stale.api.STM_Fmp4_Seek(stale.handle, 180); stale.unblock(); await stale.settle();
  assert.equal(stale.attempts.filter(x => x.file === 'audio-9').length, 0, 'Old fetch must not append after seeking');
  stale.api.STM_Fmp4_Destroy(stale.handle); await stale.tick();
  assert.equal(stale.player.destroyed, true);
  console.log('PASS Web audio: sequence-free growing playlist, file-count prefetch/exact PTS/history, quota retry, forward/backward seek, stale fetch and destroy');
})().catch(error => {console.error(error); process.exitCode=1;});
