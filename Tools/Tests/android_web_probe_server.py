"""USB-localhost Web receiver diagnostics without remote browser automation."""
import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from streamingmesh_dev_server import Handler, StreamingMeshServer

HOOK = r'''<script>
(() => {
  const session = Date.now().toString(36);
  const queue = [];
  function record(kind, value) {
    if (queue.length < 256) queue.push({kind, value, seconds:performance.now()/1000, session});
  }
  for (const kind of ['log','warn','error']) {
    const original = console[kind].bind(console);
    console[kind] = (...args) => {
      original(...args);
      const message = args.map(a => String(a)).join(' ');
      if (kind !== 'log' || /STM_|StreamingMesh|texture|WebGPU|WebGL|graphics|error|memory|Exception/i.test(message))
        record(kind, message);
    };
  }
  window.addEventListener('error', e => record('page-error', e.message));
  window.addEventListener('unhandledrejection', e => record('rejection', String(e.reason)));
  if (window.SourceBuffer) {
    const append = SourceBuffer.prototype.appendBuffer;
    SourceBuffer.prototype.appendBuffer = function(bytes) {
      try { return append.call(this, bytes); }
      catch (error) {
        const ranges = [];
        for (let i=0;i<this.buffered.length;i++) ranges.push([this.buffered.start(i),this.buffered.end(i)]);
        record('append-error', {name:error.name,message:error.message,bytes:bytes.byteLength,ranges});
        throw error;
      }
    };
  }
  record('page', {userAgent:navigator.userAgent, crossOriginIsolated, webgpu:!!navigator.gpu});
  setInterval(() => {
    const audio = document.querySelector('audio');
    if (audio) record('audio-window', {
      time:audio.currentTime, duration:audio.duration, paused:audio.paused,
      ended:audio.ended, readyState:audio.readyState,
      ranges:Array.from({length:audio.buffered.length}, (_, i) => [audio.buffered.start(i),audio.buffered.end(i)])
    });
    if (!queue.length) return;
    const batch = queue.splice(0);
    fetch('/__android_probe', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(batch)})
      .catch(() => { if (queue.length < 128) queue.unshift(...batch); });
  }, 2000);
})();
</script>'''


class ProbeHandler(Handler):
    def _serve_file(self, root, relative):
        if root == self.server.web_root and relative == "index.html":
            source = (root / relative).read_text(encoding="utf-8-sig")
            if source.count("</head>") != 1:
                self.send_error(500, "Missing HTML head")
                return
            data = source.replace("</head>", HOOK + "</head>").encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        super()._serve_file(root, relative)

    def do_POST(self):
        if self.path != "/__android_probe":
            super().do_POST()
            return
        size = int(self.headers.get("Content-Length", "0"))
        if not 0 < size <= 1024 * 1024:
            self.send_error(413)
            return
        try:
            values = json.loads(self.rfile.read(size))
            if not isinstance(values, list) or len(values) > 512:
                raise ValueError("Invalid diagnostic batch")
        except (ValueError, UnicodeDecodeError):
            self.send_error(400)
            return
        with self.server.publish_lock:
            with self.server.probe_log.open("a", encoding="utf-8") as output:
                for value in values:
                    output.write(json.dumps({"received": datetime.now(timezone.utc).isoformat(), "event": value}) + "\n")
        self.send_response(204)
        self.end_headers()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8006)
    parser.add_argument("--web-root", type=Path, default=Path("Builds/MultiFormatReceiver"))
    parser.add_argument("--data-root", type=Path, default=Path("DevData/channels"))
    parser.add_argument("--log", type=Path, default=Path("Logs/AndroidWebReceiver-events.jsonl"))
    args = parser.parse_args()
    args.log.parent.mkdir(parents=True, exist_ok=True)
    server = StreamingMeshServer(("127.0.0.1", args.port), ProbeHandler, args.web_root, args.data_root, "")
    server.probe_log = args.log
    print("Android Web probe on localhost:" + str(args.port), flush=True)
    server.serve_forever()
