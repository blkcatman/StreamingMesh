"""Publish an existing v6 recording incrementally through the Sender HTTP API.

This tests a growing channel; it does not record new animation or audio.
Run --prepare once, then raise --through to expose additional complete chunks.
"""
import argparse
import json
import os
import urllib.request
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--source", type=Path, default=Path("DevData/channels/channel_KAGURA_MULTI_V6"))
parser.add_argument("--port", type=int, default=8006)
parser.add_argument("--channel", default="channel_KAGURA_LIVE_V6")
parser.add_argument("--through", type=int, required=True, help="Number of vertex and audio chunks to publish")
parser.add_argument("--prepare", action="store_true")
args = parser.parse_args()
if not args.channel.isascii() or not args.channel.startswith("channel_") or not args.channel.replace("_", "").isalnum():
    raise SystemExit("Use an ASCII channel name without paths")
source = args.source.resolve()
target = source.parent / args.channel
state_path = Path("Logs") / (args.channel + "-publisher.json")
base = f"http://127.0.0.1:{args.port}/channels/{args.channel}/"
read_json = lambda path: json.loads(path.read_text(encoding="utf-8-sig"))
info = read_json(source / "stream.json")
assert info["protocolVersion"] == 6
rows = lambda name: [json.loads(line) for line in (source / name).read_text().splitlines() if line.strip()]
vertices, audio = rows(info["streamInfo"]), rows(info["audioInfo"])
if not 0 <= args.through <= min(len(vertices), len(audio)):
    raise SystemExit("Chunk count outside the recording")

def post(query, data, token=None):
    headers = {"Authorization": "Bearer " + token} if token else {}
    request = urllib.request.Request(base + "?" + query, data=data, headers=headers)
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)

if args.prepare:
    if target.exists() or state_path.exists():
        raise SystemExit("Live fixture already exists; choose a new --channel")
    state = {"token": post("channel=" + args.channel, b'{"protocolVersion":6,"initialData":[]}')["push_token"], "count": 0}
    names = {part["file"] for part in info["initialData"]}
    for variant in info.get("textureVariants", []):
        names.update(part["file"] for part in variant["initialData"])
    for name in names:
        os.link(source / name, target / name)
    post("audio=" + info["audioInit"], (source / info["audioInit"]).read_bytes(), state["token"])
    post("initialinfo=stream.json", json.dumps(info).encode(), state["token"])
else:
    state = read_json(state_path)
if args.through < state["count"]:
    raise SystemExit("Live publication must be append-only")
for index in range(state["count"], args.through):
    # Completed media is visible before its metadata row, just as in Sender.
    for query, row, field, playlist in (("stream", vertices[index], "video", "streaminfo"),
                                        ("audio", audio[index], "audio", "audioinfo")):
        post(query + "=" + row[field], (source / row[field]).read_bytes(), state["token"])
        post(playlist + "=" + str(index), json.dumps(row).encode(), state["token"])
    state["count"] = index + 1
    state_path.parent.mkdir(exist_ok=True)
    state_path.write_text(json.dumps(state), encoding="utf-8")
# Publish an open-ended HLS playlist for native audio; Web uses stream.stma.
hls = ['#EXTM3U', '#EXT-X-VERSION:7', '#EXT-X-TARGETDURATION:10', '#EXT-X-MEDIA-SEQUENCE:0',
       '#EXT-X-MAP:URI="' + info["audioInit"] + '"', '#EXT-X-DISCONTINUITY']
for row in audio[:args.through]:
    hls += [f'#EXTINF:{(row["endTicks"] - row["startTicks"]) / info["timebaseHz"]:.6f},', row["audio"]]
post("audio=" + info["audioPlaylist"], ("\n".join(hls) + "\n").encode(), state["token"])
state_path.parent.mkdir(exist_ok=True)
state_path.write_text(json.dumps(state), encoding="utf-8")
print(json.dumps({"channel": args.channel, "publishedChunks": args.through,
                  "vertexEndSeconds": vertices[args.through - 1]["endTicks"] / info["timebaseHz"] if args.through else 0}))
