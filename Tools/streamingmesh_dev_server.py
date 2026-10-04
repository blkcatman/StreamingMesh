#!/usr/bin/env python3
"""Local StreamingMesh channel API and Web receiver host for development."""

from __future__ import annotations

import argparse
import html
import json
import hashlib
import mimetypes
import os
import re
import secrets
import tempfile
import threading
import time
from datetime import datetime
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, quote, unquote, urlparse


CHANNEL_PATTERN = re.compile(r"^[A-Za-z0-9_-]+$")


class StreamingMeshServer(ThreadingHTTPServer):
    def __init__(
        self,
        address,
        handler,
        web_root: Path,
        data_root: Path,
        provision_token: str,
    ):
        super().__init__(address, handler)
        self.web_root = web_root.resolve()
        self.data_root = data_root.resolve()
        self.provision_token = provision_token
        self.auth_tokens: dict[str, str] = {}
        self.publish_lock = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    server: StreamingMeshServer

    def end_headers(self) -> None:
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
        self.send_header("Cross-Origin-Resource-Policy", "cross-origin")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "Authorization, Content-Type")
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def do_OPTIONS(self) -> None:
        self.send_response(HTTPStatus.NO_CONTENT)
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.end_headers()

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        if parsed.path == "/":
            self.send_response(HTTPStatus.TEMPORARY_REDIRECT)
            self.send_header("Location", "/viewer/")
            self.end_headers()
            return

        if parsed.path == "/viewer/" and "channel" not in parse_qs(
            parsed.query, keep_blank_values=True
        ):
            self._serve_channel_index()
            return

        if parsed.path.startswith("/viewer/"):
            relative = parsed.path[len("/viewer/"):] or "index.html"
            self._serve_file(self.server.web_root, relative)
            return

        channel, relative = self._parse_channel_path(parsed.path)
        if channel is not None and relative:
            self._serve_file(self.server.data_root / channel, relative)
            return

        self.send_error(HTTPStatus.NOT_FOUND)

    def _serve_channel_index(self) -> None:
        channels = self._discover_channels()
        scheme = self.headers.get("X-Forwarded-Proto", "http").split(",", 1)[0].strip()
        if scheme not in ("http", "https"):
            scheme = "http"
        host = self.headers.get("Host", "")
        if not re.fullmatch(r"[A-Za-z0-9.\-:\[\]]+", host):
            server_host, server_port = self.server.server_address[:2]
            host = f"{server_host}:{server_port}"

        cards = []
        for channel in channels:
            channel_url = f"{scheme}://{host}/channels/{quote(channel['name'])}/"
            viewer_url = "/viewer/?channel=" + quote(channel_url, safe="")
            details = [
                f"protocol {channel['protocol_version']}",
                f"{channel['fps']} fps",
            ]
            if channel["audio_format"]:
                details.append(channel["audio_format"])
            cards.append(
                '<li class="channel">'
                f'<a href="{html.escape(viewer_url, quote=True)}">'
                f'<strong>{html.escape(channel["name"])}</strong>'
                '<span class="status">受信可能</span>'
                f'<span class="details">{html.escape(" · ".join(details))}</span>'
                f'<time>更新: {html.escape(channel["updated_at"])}</time>'
                "</a></li>"
            )

        if cards:
            channel_content = '<ul class="channels">' + "".join(cards) + "</ul>"
        else:
            channel_content = (
                '<p class="empty">受信可能なチャンネルはありません。'
                "送信側で Create Channel を実行してください。</p>"
            )

        payload = f"""<!doctype html>
<html lang="ja">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>StreamingMesh Channels</title>
  <style>
    :root {{ color-scheme: dark; font-family: system-ui, sans-serif; }}
    body {{ margin: 0; background: #0c111b; color: #e8edf6; }}
    main {{ width: min(760px, calc(100% - 32px)); margin: 64px auto; }}
    header {{ display: flex; align-items: end; justify-content: space-between; gap: 16px; }}
    h1 {{ margin: 0; font-size: clamp(28px, 5vw, 44px); }}
    .refresh {{ color: #9cc4ff; text-decoration: none; }}
    .channels {{ display: grid; gap: 12px; padding: 0; margin: 28px 0; list-style: none; }}
    .channel a {{ display: grid; grid-template-columns: 1fr auto; gap: 8px 16px;
      padding: 18px 20px; border: 1px solid #26344b; border-radius: 12px;
      color: inherit; background: #131c2b; text-decoration: none; }}
    .channel a:hover, .channel a:focus-visible {{ border-color: #6fa9ff; background: #17243a; }}
    .channel strong {{ font-size: 20px; overflow-wrap: anywhere; }}
    .status {{ align-self: center; color: #72e2a5; font-size: 13px; }}
    .details, time {{ color: #aab7ca; font-size: 13px; }}
    time {{ text-align: right; }}
    .empty {{ margin-top: 28px; padding: 24px; border: 1px dashed #394861;
      border-radius: 12px; color: #aab7ca; }}
  </style>
</head>
<body>
  <main>
    <header><div><h1>StreamingMesh</h1><p>受信可能なチャンネル</p></div>
      <a class="refresh" href="/viewer/">更新</a></header>
    {channel_content}
  </main>
</body>
</html>""".encode("utf-8")
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _discover_channels(self):
        channels = []
        try:
            roots = sorted(self.server.data_root.iterdir(), key=lambda path: path.name.lower())
        except OSError:
            return channels

        for root in roots:
            if not root.is_dir() or not CHANNEL_PATTERN.fullmatch(root.name):
                continue
            info_path = root / "stream.json"
            try:
                info = json.loads(info_path.read_text(encoding="utf-8"))
                if info.get("protocol_version") == 5:
                    parts = info.get("initial_data")
                    if not parts:
                        continue
                    for variant in info.get("texture_variants") or []:
                        parts = parts + variant["initial_data"]
                    data_paths = [self._safe_path(root, part["file"]) for part in parts]
                    if any(not path.is_file() or path.stat().st_size != part["compressedSize"]
                           for path, part in zip(data_paths, parts)):
                        continue
                else:
                    data_paths = [self._safe_path(root, info.get("data", ""))]
                    if not data_paths[0].is_file():
                        continue
                interval = float(info.get("frame_interval", 0))
                fps = f"{1.0 / interval:.2f}".rstrip("0").rstrip(".") if interval > 0 else "?"
                update_candidates = [info_path, *data_paths]
                for playlist_name in (info.get("stream_info"), info.get("audio_info")):
                    if playlist_name:
                        playlist_path = self._safe_path(
                            self.server.data_root, f"{root.name}/{playlist_name}"
                        )
                        if playlist_path.is_file():
                            update_candidates.append(playlist_path)
                updated = max(path.stat().st_mtime for path in update_candidates)
            except (OSError, ValueError, TypeError, KeyError, json.JSONDecodeError):
                continue

            channels.append(
                {
                    "name": root.name,
                    "protocol_version": info.get("protocol_version", "?"),
                    "fps": fps,
                    "audio_format": str(info.get("audio_format", "")),
                    "updated_at": datetime.fromtimestamp(updated).astimezone().strftime(
                        "%Y-%m-%d %H:%M:%S"
                    ),
                }
            )
        return channels

    def do_POST(self) -> None:
        parsed = urlparse(self.path)
        channel, relative = self._parse_channel_path(parsed.path)
        if channel is None or relative:
            self.send_error(HTTPStatus.NOT_FOUND)
            return

        query = parse_qs(parsed.query, keep_blank_values=True)
        if "channel" in query:
            if self.server.provision_token and not self._has_bearer_token(
                self.server.provision_token
            ):
                self._send_unauthorized("Invalid channel provisioning token")
                return
            body = self._read_request_body()
            channel_root = (self.server.data_root / channel).resolve()
            channel_root.mkdir(parents=True, exist_ok=True)
            self._create_channel(channel, channel_root, body)
            return

        expected_token = self.server.auth_tokens.get(channel)
        if not expected_token or not self._has_bearer_token(expected_token):
            self._send_unauthorized("Invalid channel push token")
            return

        body = self._read_request_body()
        channel_root = (self.server.data_root / channel).resolve()
        channel_root.mkdir(parents=True, exist_ok=True)
        try:
            if "initialinfo" in query:
                if query["initialinfo"] != ["stream.json"]:
                    raise ValueError("Initial manifest must be stream.json")
                self._publish_initial_data(channel_root, body)
            elif "combined" in query:
                self._write_file(channel_root, query["combined"][0], body)
            elif "stream" in query:
                self._write_file(channel_root, query["stream"][0], body)
            elif "streaminfo" in query:
                self._append_playlist(channel_root / "stream.stmj", body)
            elif "audio" in query:
                self._write_file(channel_root, query["audio"][0], body)
            elif "audioinit" in query:
                self._write_file(channel_root, query["audioinit"][0], body)
            elif "audioinfo" in query:
                self._append_playlist(channel_root / "stream.stma", body)
            else:
                self.send_error(HTTPStatus.BAD_REQUEST, "Unknown StreamingMesh upload")
                return
        except ValueError as exception:
            self.send_error(HTTPStatus.BAD_REQUEST, str(exception))
            return

        self._send_json({"ok": True})

    def _publish_initial_data(self, root: Path, body: bytes) -> None:
        """Commit a v5 manifest only after all named, hashed parts exist."""
        try:
            info = json.loads(body)
            parts = info["initial_data"]
            if info["protocol_version"] != 5 or not isinstance(parts, list) or not 1 <= len(parts) <= 4096:
                raise ValueError("Invalid initial manifest")
            variants = info.get("texture_variants") or []
            if not isinstance(variants, list) or len(variants) > 7:
                raise ValueError("Invalid texture variants")
            formats = set()
            groups = [parts]
            for variant in variants:
                if variant["format"] not in {"BC7", "DXT1", "DXT5", "ETC2_RGB", "ETC2_RGBA8", "ASTC_4x4", "ASTC_6x6"} or variant["format"] in formats:
                    raise ValueError("Invalid/duplicate texture variant format")
                formats.add(variant["format"])
                group = variant["initial_data"]
                if not isinstance(group, list) or not 1 <= len(group) <= 4096:
                    raise ValueError("Invalid variant files")
                if len({part["file"].lower() for part in group}) != len(group):
                    raise ValueError("Duplicate variant filename")
                groups.append(group)
            if len({part["file"].lower() for part in parts}) != len(parts):
                raise ValueError("Duplicate initial filename")
            files = {}
            for group in groups:
                for part in group:
                    name = part["file"].lower()
                    if name in files and files[name] != part:
                        raise ValueError("Conflicting shared initial file")
                    files[name] = part
            for part in files.values():
                name = part["file"]
                if not re.fullmatch(r"[A-Za-z0-9_-][A-Za-z0-9_.-]{0,91}\.bin", name):
                    raise ValueError("Invalid or duplicate initial filename")
                if not 0 < part["size"] <= 128 * 1024 * 1024 or not 0 < part["compressedSize"] <= 128 * 1024 * 1024 + 65536:
                    raise ValueError("Invalid initial part size")
                path = self._safe_path(root, name)
                with path.open("rb") as source:
                    if os.fstat(source.fileno()).st_size != part["compressedSize"]:
                        raise ValueError("Missing or incomplete initial part: " + name)
                    digest = hashlib.sha256()
                    while block := source.read(65536):
                        digest.update(block)
                if digest.hexdigest() != part["sha256"]:
                    raise ValueError("Changed initial part: " + name)
        except (OSError, KeyError, TypeError, json.JSONDecodeError) as error:
            raise ValueError("Invalid/missing initial resources") from error
        self._atomic_write(root / "stream.json", body)

    def _create_channel(self, channel: str, root: Path, body: bytes) -> None:
        token = secrets.token_urlsafe(24)
        self.server.auth_tokens[channel] = token
        for playlist_name in ("stream.stmj", "stream.stma"):
            playlist = root / playlist_name
            if playlist.exists():
                playlist.unlink()
        for pattern in ("audio-init.mp4", "audio-*.m4s", "audio.m3u8"):
            for stale_audio in root.glob(pattern):
                stale_audio.unlink()
        self._atomic_write(root / "stream.json", body)
        self._send_json({"push_token": token})

    def _has_bearer_token(self, expected_token: str) -> bool:
        authorization = self.headers.get("Authorization", "")
        scheme, separator, supplied_token = authorization.partition(" ")
        return (
            separator == " "
            and scheme.lower() == "bearer"
            and bool(supplied_token)
            and secrets.compare_digest(expected_token, supplied_token)
        )

    def _read_request_body(self) -> bytes:
        content_length = int(self.headers.get("Content-Length", "0"))
        return self.rfile.read(content_length)

    def _send_unauthorized(self, message: str) -> None:
        payload = message.encode("utf-8")
        self.send_response(HTTPStatus.UNAUTHORIZED)
        self.send_header("WWW-Authenticate", 'Bearer realm="StreamingMesh"')
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _parse_channel_path(self, path: str):
        parts = [unquote(part) for part in path.split("/") if part]
        if len(parts) < 2 or parts[0] != "channels":
            return None, None
        channel = parts[1]
        if not CHANNEL_PATTERN.fullmatch(channel):
            return None, None
        return channel, "/".join(parts[2:])

    def _serve_file(self, root: Path, relative: str) -> None:
        try:
            target = self._safe_path(root, relative)
        except ValueError:
            self.send_error(HTTPStatus.BAD_REQUEST)
            return
        if target.is_dir():
            target = target / "index.html"
        if not target.is_file():
            self.send_error(HTTPStatus.NOT_FOUND)
            return

        content_type = mimetypes.guess_type(target.name)[0] or "application/octet-stream"
        if target.suffix == ".wasm":
            content_type = "application/wasm"
        elif target.suffix == ".m3u8":
            content_type = "application/vnd.apple.mpegurl"
        elif target.suffix in (".mp4", ".m4s") and target.name.startswith("audio"):
            content_type = "audio/mp4"
        # Open first, then obtain the length from that same inode. An atomic
        # publisher may replace the path while this response is being sent.
        for attempt in range(100):
            try:
                source = target.open("rb")
                break
            except FileNotFoundError:
                self.send_error(HTTPStatus.NOT_FOUND)
                return
            except PermissionError:
                if os.name != "nt" or attempt == 99:
                    raise
                time.sleep(0.05)
        with source:
            remaining = os.fstat(source.fileno()).st_size
            self.send_response(HTTPStatus.OK)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(remaining))
            self.end_headers()
            while remaining:
                chunk = source.read(min(64 * 1024, remaining))
                if not chunk:
                    break
                self.wfile.write(chunk)
                remaining -= len(chunk)

    @staticmethod
    def _atomic_write(target: Path, body: bytes) -> None:
        target.parent.mkdir(parents=True, exist_ok=True)
        descriptor, temporary = tempfile.mkstemp(prefix=".upload-", dir=target.parent)
        try:
            with os.fdopen(descriptor, "wb") as output:
                output.write(body)
            # Windows readers can temporarily deny replacement. Keep the old
            # complete file visible and retry; never truncate it in place.
            for attempt in range(100):
                try:
                    os.replace(temporary, target)
                    break
                except PermissionError:
                    if os.name != "nt" or attempt == 99:
                        raise
                    time.sleep(0.05)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)

    def _write_file(self, root: Path, relative: str, body: bytes) -> None:
        self._atomic_write(self._safe_path(root, relative), body)

    def _append_playlist(self, target: Path, body: bytes) -> None:
        # Readers always see complete records, and concurrent POSTs cannot lose
        # one another's playlist entries during read-modify-replace.
        with self.server.publish_lock:
            existing = target.read_bytes() if target.exists() else b""
            self._atomic_write(target, existing + body.rstrip(b"\r\n") + b"\n")

    @staticmethod
    def _safe_path(root: Path, relative: str) -> Path:
        if not relative or "\x00" in relative:
            raise ValueError("Invalid empty path")
        target = (root / relative).resolve()
        root_text = str(root.resolve())
        target_text = str(target)
        if target_text != root_text and not target_text.startswith(root_text + os.sep):
            raise ValueError("Path escapes the configured root")
        return target

    def _send_json(self, value) -> None:
        payload = json.dumps(value).encode("utf-8")
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)


def main() -> None:
    project_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--web-root", type=Path, default=project_root / "Builds" / "WebReceiver")
    parser.add_argument("--data-root", type=Path, default=project_root / "DevData" / "channels")
    parser.add_argument(
        "--provision-token",
        default=os.environ.get("STREAMINGMESH_PROVISION_TOKEN", ""),
        help="Bearer token required to create or reset channels",
    )
    arguments = parser.parse_args()

    loopback_hosts = {"127.0.0.1", "localhost", "::1"}
    if arguments.host not in loopback_hosts and not arguments.provision_token:
        parser.error(
            "--provision-token or STREAMINGMESH_PROVISION_TOKEN is required "
            "when binding outside localhost"
        )

    arguments.web_root.mkdir(parents=True, exist_ok=True)
    arguments.data_root.mkdir(parents=True, exist_ok=True)
    server = StreamingMeshServer(
        (arguments.host, arguments.port),
        Handler,
        arguments.web_root,
        arguments.data_root,
        arguments.provision_token,
    )
    print(f"StreamingMesh dev server: http://{arguments.host}:{arguments.port}/viewer/")
    print(f"Channel API: http://{arguments.host}:{arguments.port}/channels/")
    if not arguments.provision_token:
        print("Channel provisioning: unauthenticated localhost development mode")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
