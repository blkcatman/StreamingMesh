"""HTTP regression: replacing media/playlist files never exposes partial content."""
import concurrent.futures
import importlib.util
import json
import tempfile
import threading
import unittest
import urllib.request
from pathlib import Path

spec = importlib.util.spec_from_file_location("stm_server", Path(__file__).parents[1] / "streamingmesh_dev_server.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class QuietHandler(module.Handler):
    def log_message(self, *args): pass

class PublicationTest(unittest.TestCase):
    def test_atomic_media_and_playlists(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            server = module.StreamingMeshServer(("127.0.0.1", 0), QuietHandler, root, root, "")
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            base = f"http://127.0.0.1:{server.server_port}/channels/channel_test/"
            def post(query, data, token=None):
                headers = {"Authorization": "Bearer " + token} if token else {}
                with urllib.request.urlopen(urllib.request.Request(base + "?" + query, data, headers), timeout=10) as r:
                    return json.load(r)
            try:
                token = post("channel=channel_test", b"{}")['push_token']
                variants = [bytes([i+1]) * size for i, size in enumerate([127, 2_000_000, 93_731, 1_000_003])]
                post("stream=000000.stmv", variants[0], token)
                def writer():
                    for i in range(60): post("stream=000000.stmv", variants[i%4], token)
                def reader():
                    for _ in range(80):
                        with urllib.request.urlopen(base+"000000.stmv", timeout=10) as r:
                            content=r.read()
                            self.assertEqual(len(content), int(r.headers['Content-Length']))
                            self.assertIn(content, variants)
                with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
                    futures=[pool.submit(writer)]+[pool.submit(reader) for _ in range(3)]
                    for future in futures: future.result()
                with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
                    list(pool.map(lambda i: post("streaminfo="+str(i), json.dumps({'video': str(i)+'.stmv'}).encode(), token), range(40)))
                with urllib.request.urlopen(base+"stream.stmj") as response:
                    lines=response.read().splitlines()
                self.assertEqual(40,len(lines))
                self.assertEqual(40,len({json.loads(line)['video'] for line in lines}))
            finally:
                server.shutdown();server.server_close();thread.join()

if __name__ == '__main__': unittest.main()
