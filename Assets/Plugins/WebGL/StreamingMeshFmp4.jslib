mergeInto(LibraryManager.library, {
  STM_Fmp4_Create: function(channelUrlPtr, initFilePtr, playlistFilePtr, mimeTypePtr) {
    var root = Module.StreamingMeshFmp4;
    if (!root) {
      root = Module.StreamingMeshFmp4 = { nextHandle: 1, players: {} };
    }

    var baseUrl = UTF8ToString(channelUrlPtr);
    var initFile = UTF8ToString(initFilePtr);
    var playlistFile = UTF8ToString(playlistFilePtr);
    var mimeType = UTF8ToString(mimeTypePtr);
    if (!window.MediaSource || !MediaSource.isTypeSupported(mimeType)) {
      console.error("StreamingMesh fMP4 MIME type is unsupported: " + mimeType);
      return 0;
    }

    var handle = root.nextHandle++;
    var audio = document.createElement("audio");
    audio.preload = "auto";
    audio.style.display = "none";
    audio.setAttribute("playsinline", "");
    document.body.appendChild(audio);

    var mediaSource = new MediaSource();
    var objectUrl = URL.createObjectURL(mediaSource);
    audio.src = objectUrl;
    var player = root.players[handle] = {
      audio: audio,
      mediaSource: mediaSource,
      objectUrl: objectUrl,
      sourceBuffer: null,
      queue: [],
      seen: {},
      pending: {},
      generation: 0,
      failed: false,
      quotaBlocked: false,
      aheadSeconds: 60,
      backSeconds: 30,
      maximumSegmentSeconds: 0,
      destroyed: false,
      fetching: false,
      playbackRequested: false,
      state: 0,
      timer: 0,
      gestureHandler: null
    };

    function pump() {
      if (player.destroyed || player.failed || !player.sourceBuffer || player.sourceBuffer.updating)
        return;
      // Keep a bounded playback window, including after seeking backwards.
      var ranges = player.sourceBuffer.buffered;
      var before = Math.max(0, audio.currentTime - (player.quotaBlocked ? Math.min(2, player.backSeconds) : player.backSeconds));
      var after = audio.currentTime + player.aheadSeconds + player.maximumSegmentSeconds;
      if (ranges.length && ranges.start(0) < before - 0.25) {
        player.sourceBuffer.remove(0, before);
        return;
      }
      if (ranges.length && ranges.end(ranges.length - 1) > after + 0.25) {
        player.sourceBuffer.remove(after, ranges.end(ranges.length - 1));
        return;
      }
      if (!player.queue.length) return;
      var item = player.queue[0];
      try {
        player.sourceBuffer.appendBuffer(item.bytes);
        player.queue.shift();
        if (item.file) {
          delete player.pending[item.file];
          player.seen[item.file] = true;
        }
        player.quotaBlocked = false;
      } catch (error) {
        if (error.name === "QuotaExceededError") {
          // appendBuffer did not consume the data. Retry the same head once
          // playback/eviction makes room; do not turn a full buffer into a fatal state.
          if (!player.quotaBlocked) console.warn("StreamingMesh fMP4 waiting for buffer space: " + error.message);
          player.quotaBlocked = true;
          return;
        }
        player.failed = true;
        player.state = -1;
        console.error("StreamingMesh fMP4 append failed: " + error.name + ": " + error.message);
      }
    }

    function contains(start, end) {
      var ranges = player.sourceBuffer.buffered;
      for (var range = 0; range < ranges.length; ++range)
        if (ranges.start(range) <= start + 0.25 && ranges.end(range) >= end - 0.25) return true;
      return false;
    }

    function requestPlayback() {
      if (player.destroyed || !player.playbackRequested || !audio.paused || audio.seeking)
        return;
      var promise = audio.play();
      if (promise && promise.catch) {
        promise.catch(function() {
          player.state = 1;
        });
      }
    }

    player.gestureHandler = function() {
      requestPlayback();
    };
    player.requestPlayback = requestPlayback;
    document.addEventListener("pointerdown", player.gestureHandler, true);
    document.addEventListener("keydown", player.gestureHandler, true);

    async function fetchPlaylist() {
      if (player.destroyed || player.failed || player.fetching || !player.sourceBuffer || player.sourceBuffer.updating ||
          player.quotaBlocked || player.queue.length >= 2)
        return;
      player.fetching = true;
      var generation = player.generation;
      try {
        var response = await fetch(baseUrl + playlistFile, { cache: "no-store" });
        if (!response.ok)
          return;
        var text = await response.text();
        if (player.destroyed || generation !== player.generation) return;
        var entries = text.split(/\r?\n/).filter(Boolean).map(function(line) {
          return JSON.parse(line);
        }).sort(function(a, b) {
          return a.startTicks - b.startTicks;
        });
        player.maximumSegmentSeconds = entries.reduce(function(maximum, entry) {
          var start = entry.startTicks / 10000000, end = entry.endTicks / 10000000;
          if (!Number.isFinite(start) || !Number.isFinite(end) || start < 0 || end <= start)
            throw new Error("Invalid fMP4 segment timestamps");
          return Math.max(maximum, end - start);
        }, 0);
        // Preserve the full seekable timeline while downloading only a window.
        // MediaSource otherwise exposes only the end of the appended prefix.
        var duration = entries.length ? entries[entries.length - 1].endTicks / 10000000 : 0;
        if (!player.sourceBuffer.updating && Number.isFinite(duration) && duration > 0 &&
            (!Number.isFinite(mediaSource.duration) || duration > mediaSource.duration))
          mediaSource.duration = duration;

        for (var i = 0; i < entries.length; ++i) {
          var entry = entries[i];
          var start = entry.startTicks / 10000000;
          var end = entry.endTicks / 10000000;
          if (!Number.isFinite(start) || !Number.isFinite(end) || start < 0 || end <= start)
            throw new Error("Invalid fMP4 segment timestamps");
          var before = audio.currentTime;
          if (!entry.audio || end <= before || player.pending[entry.audio])
            continue;
          if (start > audio.currentTime + player.aheadSeconds || player.queue.length >= 2 || player.quotaBlocked) break;
          if (player.seen[entry.audio] && contains(Math.max(start, before), end)) continue;
          var segmentResponse = await fetch(baseUrl + entry.audio, { cache: "no-store" });
          if (!segmentResponse.ok)
            break;
          var bytes = await segmentResponse.arrayBuffer();
          if (player.destroyed || generation !== player.generation) return;
          player.pending[entry.audio] = true;
          player.queue.push({ bytes: bytes, file: entry.audio, start: start });
          pump();
        }
      } catch (error) {
        console.warn("StreamingMesh audio playlist is not ready", error);
      } finally {
        player.fetching = false;
      }
    }

    mediaSource.addEventListener("sourceopen", async function() {
      if (player.destroyed)
        return;
      try {
        player.sourceBuffer = mediaSource.addSourceBuffer(mimeType);
        player.sourceBuffer.mode = "segments";
        player.sourceBuffer.addEventListener("updateend", function() {
          pump();
          // Removal is asynchronous too. Resume fetching once it finishes so
          // periodic trimming cannot starve the forward playback window.
          fetchPlaylist();
          if (audio.buffered.length > 0) {
            player.state = 1;
            requestPlayback();
          }
        });
        var initResponse = null;
        while (!player.destroyed) {
          try {
            var candidate = await fetch(baseUrl + initFile, { cache: "no-store" });
            if (candidate.ok) {
              initResponse = candidate;
              break;
            }
          } catch (ignored) {
          }
          await new Promise(function(resolve) { window.setTimeout(resolve, 500); });
        }
        if (!initResponse)
          return;
        var bytes = await initResponse.arrayBuffer();
        if (player.destroyed) return;
        player.queue.push({ bytes: bytes, file: null });
        pump();
        await fetchPlaylist();
        if (!player.destroyed) player.timer = window.setInterval(function() { pump(); fetchPlaylist(); }, 500);
      } catch (error) {
        player.state = -1;
        console.error("StreamingMesh fMP4 initialization failed", error);
      }
    }, { once: true });

    player.configureBuffering = function(ahead, back) {
      if (!Number.isFinite(ahead) || ahead < 0.5 || ahead > 120 ||
          !Number.isFinite(back) || back < 0 || back > 120) return;
      if (player.aheadSeconds === ahead && player.backSeconds === back) return;
      player.aheadSeconds = ahead; player.backSeconds = back;
      player.generation++; // Discard a fetch started with the previous window.
      player.queue = player.queue.filter(function(item) {
        if (!item.file || item.start <= audio.currentTime + ahead) return true;
        delete player.pending[item.file]; return false;
      });
      // A retained blocked head gets another attempt after old ranges are removed.
      if (!player.queue.length) player.quotaBlocked = false;
      pump(); fetchPlaylist();
    };

    audio.addEventListener("playing", function() { player.state = 2; });
    audio.addEventListener("waiting", function() { player.state = 1; });
    audio.addEventListener("ended", function() { player.state = 3; });
    audio.addEventListener("error", function() { player.state = -1; });
    return handle;
  },

  STM_Fmp4_GetTime: function(handle) {
    var root = Module.StreamingMeshFmp4;
    var player = root && root.players[handle];
    return player && player.state >= 1 && !player.audio.seeking ? player.audio.currentTime : -1.0;
  },

  STM_Fmp4_ConfigureBuffering: function(handle, aheadSeconds, backSeconds) {
    var root = Module.StreamingMeshFmp4;
    var player = root && root.players[handle];
    if (player && !player.destroyed) player.configureBuffering(aheadSeconds, backSeconds);
  },

  STM_Fmp4_GetState: function(handle) {
    var root = Module.StreamingMeshFmp4;
    var player = root && root.players[handle];
    return player ? player.state : 0;
  },

  STM_Fmp4_Seek: function(handle, time) {
    var root = Module.StreamingMeshFmp4;
    var player = root && root.players[handle];
    if (player) {
      player.playbackRequested = false;
      player.audio.pause();
      player.audio.currentTime = Math.max(0, time);
      player.generation++;
    }
  },

  STM_Fmp4_SetPlaying: function(handle, playing) {
    var root = Module.StreamingMeshFmp4;
    var player = root && root.players[handle];
    if (!player)
      return;
    player.playbackRequested = !!playing;
    if (playing)
      player.requestPlayback();
    else
      player.audio.pause();
  },

  STM_Fmp4_Destroy: function(handle) {
    var root = Module.StreamingMeshFmp4;
    var player = root && root.players[handle];
    if (!player)
      return;
    player.destroyed = true;
    if (player.timer)
      window.clearInterval(player.timer);
    document.removeEventListener("pointerdown", player.gestureHandler, true);
    document.removeEventListener("keydown", player.gestureHandler, true);
    player.audio.pause();
    player.audio.removeAttribute("src");
    player.audio.load();
    player.audio.remove();
    URL.revokeObjectURL(player.objectUrl);
    delete root.players[handle];
  }
});
