// Native browser API mock regression test; actual MSE playback needs a browser.
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const listeners = {};
let plays = 0;
const audio = {
  paused: true, seeking: false, currentTime: 0, style: {}, buffered: {length: 1},
  setAttribute() {}, addEventListener(name, fn) { listeners[name] = fn; },
  play() { plays++; this.paused = false; return Promise.resolve(); },
  pause() { this.paused = true; }, removeAttribute() {}, load() {}, remove() {}
};
class MediaSource {
  static isTypeSupported() { return true; }
  addEventListener() {}
}
const context = {
  LibraryManager: {library: {}}, mergeInto: Object.assign,
  Module: {}, UTF8ToString: x => x, MediaSource, console,
  window: {MediaSource, clearInterval() {}},
  URL: {createObjectURL: () => 'blob:test', revokeObjectURL() {}},
  document: {createElement: () => audio, body: {appendChild() {}},
    addEventListener(name, fn) { listeners[name] = fn; }, removeEventListener() {}}
};
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../Assets/Plugins/WebGL/StreamingMeshFmp4.jslib'), 'utf8'), context);
const api = context.LibraryManager.library;
const handle = api.STM_Fmp4_Create('/test/', 'init.mp4', 'stream.stma', 'audio/mp4');
const player = context.Module.StreamingMeshFmp4.players[handle];
player.state = 1;
listeners.pointerdown();
player.requestPlayback();
assert.equal(plays, 0, 'Loading and gestures must not bypass receiver readiness');
api.STM_Fmp4_SetPlaying(handle, 1);
assert.equal(plays, 1);
api.STM_Fmp4_SetPlaying(handle, 0);
assert.equal(audio.paused, true);
listeners.pointerdown();
assert.equal(plays, 1, 'Gesture must not resume while mesh is buffering');
api.STM_Fmp4_Seek(handle, 2);
assert.equal(audio.currentTime, 2);
assert.equal(audio.paused, true, 'Seek must not autoplay');
audio.seeking = true;
assert.equal(api.STM_Fmp4_GetTime(handle), -1, 'Incomplete seek must not supply a clock');
audio.seeking = false;
api.STM_Fmp4_SetPlaying(handle, 1);
assert.equal(plays, 2);
api.STM_Fmp4_Destroy(handle);
assert.equal(audio.paused, true);
console.log('PASS: prepare paused, gesture gating, pause/resume, seek readiness, disposal');
