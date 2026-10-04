"""HTTP regression: replacing media/playlist files never exposes partial content."""
import concurrent.futures
import importlib.util
import json
import gzip
import hashlib
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
    def test_initial_manifest_commits_after_complete_parts(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            server = module.StreamingMeshServer(("127.0.0.1", 0), QuietHandler, root, root, "")
            thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
            base = f"http://127.0.0.1:{server.server_port}/channels/channel_test/"
            def post(query, data, token=None):
                headers = {"Authorization": "Bearer " + token} if token else {}
                with urllib.request.urlopen(urllib.request.Request(base + "?" + query, data, headers), timeout=10) as response:
                    return json.load(response)
            raw = b"x" * (64 * 1024 * 1024); data = gzip.compress(raw, compresslevel=1)
            filename = "c273f7e762074ee183b1264b6d854882.bin"
            part = {"file": filename, "size": len(raw), "compressedSize": len(data), "sha256": hashlib.sha256(data).hexdigest()}
            info = {"protocolVersion": 6, "initialData": [part]}
            try:
                token = post("channel=channel_test", b'{"protocolVersion":6,"initialData":[]}')['push_token']
                post("streaminfo=0", b'{"video":"000000.stmv"}', token)
                with self.assertRaises(urllib.error.HTTPError):
                    post("initialinfo=stream.json", json.dumps(info).encode(), token)
                self.assertEqual([], json.loads((root / "channel_test/stream.json").read_text())["initialData"])
                post("combined=" + filename, data, token)
                post("initialinfo=stream.json", json.dumps(info).encode(), token)
                self.assertEqual(info, json.loads((root / "channel_test/stream.json").read_text()))
                with self.assertRaises(urllib.error.HTTPError):
                    post("initialinfo=stream.json", b'{"protocol_version":5,"initial_data":[]}', token)
                self.assertTrue((root / "channel_test/stream.stmj").exists())
                extra = dict(part, file="29df64abc8a34aa4912f7cb87db2a049.bin")
                info["textureVariants"] = [{"format": "BC7", "initialData": [part]},
                                            {"format": "ASTC_4x4", "initialData": [extra]}]
                with self.assertRaises(urllib.error.HTTPError):
                    post("initialinfo=stream.json", json.dumps(info).encode(), token)
                post("combined=" + extra["file"], data, token)
                post("initialinfo=stream.json", json.dumps(info).encode(), token)
                extra["sha256"] = "0" * 64
                with self.assertRaises(urllib.error.HTTPError):
                    post("initialinfo=stream.json", json.dumps(info).encode(), token)
                extra["sha256"] = part["sha256"]
                part["size"] = 128 * 1024 * 1024 + 1
                with self.assertRaises(urllib.error.HTTPError):
                    post("initialinfo=stream.json", json.dumps(info).encode(), token)
                part["size"] = len(raw)
                post("combined=" + filename, data[:-1], token)
                with self.assertRaises(urllib.error.HTTPError):
                    post("initialinfo=stream.json", json.dumps(info).encode(), token)
                part["file"] = "../stream0.bin"
                with self.assertRaises(urllib.error.HTTPError):
                    post("initialinfo=stream.json", json.dumps(info).encode(), token)
                self.assertEqual(filename, json.loads((root / "channel_test/stream.json").read_text())["initialData"][0]["file"])
            finally:
                server.shutdown(); server.server_close(); thread.join()

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
