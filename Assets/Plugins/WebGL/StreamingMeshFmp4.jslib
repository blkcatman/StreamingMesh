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
      destroyed: false,
      fetching: false,
      state: 0,
      timer: 0,
      gestureHandler: null
    };

    function pump() {
      if (player.destroyed || !player.sourceBuffer || player.sourceBuffer.updating || !player.queue.length)
        return;
      var bytes = player.queue.shift();
      try {
        player.sourceBuffer.appendBuffer(bytes);
      } catch (error) {
        player.state = -1;
        console.error("StreamingMesh fMP4 append failed", error);
      }
    }

    function requestPlayback() {
      if (player.destroyed || !audio.paused)
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
    document.addEventListener("pointerdown", player.gestureHandler, true);
    document.addEventListener("keydown", player.gestureHandler, true);

    async function fetchPlaylist() {
      if (player.destroyed || player.fetching || !player.sourceBuffer)
        return;
      player.fetching = true;
      try {
        var response = await fetch(baseUrl + playlistFile, { cache: "no-store" });
        if (!response.ok)
          return;
        var text = await response.text();
        var entries = text.split(/\r?\n/).filter(Boolean).map(function(line) {
          return JSON.parse(line);
        }).sort(function(a, b) {
          return a.sequence - b.sequence;
        });

        for (var i = 0; i < entries.length; ++i) {
          var entry = entries[i];
          if (!entry.audio || player.seen[entry.audio])
            continue;
          var segmentResponse = await fetch(baseUrl + entry.audio, { cache: "no-store" });
          if (!segmentResponse.ok)
            break;
          player.seen[entry.audio] = true;
          player.queue.push(await segmentResponse.arrayBuffer());
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
        player.queue.push(await initResponse.arrayBuffer());
        pump();
        await fetchPlaylist();
        player.timer = window.setInterval(fetchPlaylist, 500);
      } catch (error) {
        player.state = -1;
        console.error("StreamingMesh fMP4 initialization failed", error);
      }
    }, { once: true });

    audio.addEventListener("playing", function() { player.state = 2; });
    audio.addEventListener("waiting", function() { player.state = 1; });
    audio.addEventListener("ended", function() { player.state = 3; });
    audio.addEventListener("error", function() { player.state = -1; });
    return handle;
  },

  STM_Fmp4_GetTime: function(handle) {
    var root = Module.StreamingMeshFmp4;
    var player = root && root.players[handle];
    return player && player.state >= 1 ? player.audio.currentTime : -1.0;
  },

  STM_Fmp4_GetState: function(handle) {
    var root = Module.StreamingMeshFmp4;
    var player = root && root.players[handle];
    return player ? player.state : 0;
  },

  STM_Fmp4_Seek: function(handle, time) {
    var root = Module.StreamingMeshFmp4;
    var player = root && root.players[handle];
    if (player)
      player.audio.currentTime = Math.max(0, time);
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
