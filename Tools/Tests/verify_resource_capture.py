"""Validate Sender-produced v4 initial data and complete mesh/audio recordings."""
import argparse
import gzip
import json
import re
import struct
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("channel", type=Path)
args = parser.parse_args()
root = args.channel
read_json = lambda path: json.loads(path.read_text(encoding="utf-8-sig"))
info = read_json(root / "stream.json")
assert info["protocol_version"] == 4, "Expected v4 channel"
for ids in (info["materials"], info["textures"]):
    assert len(set(ids)) == len(ids) and all(re.fullmatch("[0-9a-f]{64}", key) for key in ids)
assert len(info["textures"]) == len(info["textureNames"])
data = gzip.decompress((root / info["data"]).read_bytes())
assert len(data) <= 128 * 1024 * 1024
assert len(data) == sum(info["textureSizes"] + info["materialSizes"] + info["meshSizes"])
offset = sum(info["textureSizes"])
references = []
for key, size in zip(info["materials"], info["materialSizes"]):
    material = json.loads(data[offset:offset + size])
    assert material["version"] == 3 and material["id"] == key
    for prop in material["properties"]:
        if prop["type"] == 4 and prop.get("textureId"):
            assert prop["textureId"] in info["textures"]
            references.append(prop["textureId"])
    offset += size
vertices = 0
for size in info["meshSizes"]:
    mesh = json.loads(data[offset:offset + size])
    assert all(not key or key in info["materials"] for key in mesh["materialIds"])
    assert sum(mesh["indicesCounts"]) == len(mesh["indices"])
    assert all(0 <= index < mesh["vertexCount"] for index in mesh["indices"])
    vertices += mesh["vertexCount"]
    offset += size
assert offset == len(data)
playlist = lambda name: [json.loads(line) for line in (root / name).read_text(encoding="utf-8-sig").splitlines() if line.strip()]
chunks = playlist(info["stream_info"])
audio = playlist(info["audio_info"])
last_seq = last_pts = -1
frames = 0
for chunk in chunks:
    raw = gzip.decompress((root / chunk["video"]).read_bytes())
    count, = struct.unpack_from("<i", raw)
    sizes = struct.unpack_from("<" + "i" * count, raw, 4)
    offset = 4 + 4 * count
    assert offset + sum(sizes) == len(raw)
    for size in sizes:
        seq, = struct.unpack_from("<I", raw, offset + 1)
        pts, = struct.unpack_from("<q", raw, offset + 21)
        assert raw[offset + 8] == 2 and seq == last_seq + 1 and pts > last_pts
        last_seq, last_pts = seq, pts
        offset += size
        frames += 1
for previous, following in zip(audio, audio[1:]):
    assert previous["startSample"] + previous["sampleCount"] == following["startSample"]
assert all((root / item["audio"]).is_file() for item in audio)
print(json.dumps(dict(protocol=4, materials=len(info["materials"]), textures=len(info["textures"]),
    meshes=len(info["meshes"]), vertices=vertices, texturePropertyReferences=len(references),
    chunks=len(chunks), frames=frames, audioSegments=len(audio), meshEndSeconds=last_pts/1e7,
    audioEndSeconds=audio[-1]["endTicks"]/1e7, metadataBytes=len(data)), indent=2))
