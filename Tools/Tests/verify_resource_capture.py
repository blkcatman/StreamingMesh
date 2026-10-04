"""Validate Sender-produced v6 initial data and complete mesh/audio recordings."""
import argparse
import gzip
import hashlib
import json
import re
import struct
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("channel", type=Path)
parser.add_argument("--texture-format", help="Validate a specific GPU variant; default is the primary manifest")
args = parser.parse_args()
root = args.channel
read_json = lambda path: json.loads(path.read_text(encoding="utf-8-sig"))
info = read_json(root / "stream.json")
if args.texture_format:
    variant = next(v for v in info["textureVariants"] if v["format"] == args.texture_format)
    for key in ("textureSizes", "texturePayloads", "initialData"): info[key] = variant[key]
assert info["protocolVersion"] == 6, "Expected v6 channel"
def check_keys(value):
    if isinstance(value, dict):
        assert all(re.fullmatch(r"[a-z][A-Za-z0-9]*", key) for key in value), "Non-camelCase metadata key"
        for child in value.values(): check_keys(child)
    elif isinstance(value, list):
        for child in value: check_keys(child)
check_keys(info)
for ids in (info["materials"], info["textures"]):
    assert len(set(ids)) == len(ids) and all(re.fullmatch("[0-9a-f]{64}", key) for key in ids)
assert len(info["textures"]) == len(info["textureNames"])
resources = [[], [], []]
if info["protocolVersion"] == 4:
    data = gzip.decompress((root / info["data"]).read_bytes())
    assert len(data) <= 128 * 1024 * 1024
    assert len(data) == sum(info["textureSizes"] + info["materialSizes"] + info["meshSizes"])
    offset = 0
    for kind, sizes in ((2, info["textureSizes"]), (0, info["materialSizes"]), (1, info["meshSizes"])):
        for size in sizes:
            resources[kind].append(data[offset:offset + size] if kind != 2 else None)
            offset += size
    initial_bytes = len(data)
else:
    sizes = [info["materialSizes"], info["meshSizes"], info["textureSizes"]]
    resources = [[None] * len(table) for table in sizes]
    assert len(info["texturePayloads"]) == len(info["textures"])
    kind = index = resource_offset = initial_bytes = compressed_bytes = 0
    metadata = bytearray()
    names = set()
    for part in info["initialData"]:
        assert re.fullmatch(r"[A-Za-z0-9_-][A-Za-z0-9_.-]{0,91}\.bin", part["file"])
        assert part["file"].lower() not in names
        names.add(part["file"].lower())
        compressed = (root / part["file"]).read_bytes()
        assert len(compressed) == part["compressedSize"]
        assert hashlib.sha256(compressed).hexdigest() == part["sha256"]
        data = gzip.decompress(compressed)
        assert len(data) == part["size"] and 0 < len(data) <= 128 * 1024 * 1024
        offset = 0
        for segment in part["records"]:
            while kind < 3 and index == len(sizes[kind]):
                kind += 1; index = 0
            assert kind < 3 and segment["kind"] == kind and segment["index"] == index
            assert segment["offset"] == offset and segment["resourceOffset"] == resource_offset
            count = segment["size"]
            assert 0 < count <= len(data) - offset and count <= sizes[kind][index] - resource_offset
            if kind != 2:
                metadata.extend(data[offset:offset + count])
            resource_offset += count; offset += count
            if resource_offset == sizes[kind][index]:
                if kind != 2:
                    resources[kind][index] = bytes(metadata); metadata.clear()
                resource_offset = 0; index += 1
        assert offset == len(data)
        initial_bytes += len(data); compressed_bytes += len(compressed)
    assert resource_offset == 0 and initial_bytes == sum(sum(table) for table in sizes)
    assert initial_bytes <= 1024 * 1024 * 1024
    for payload, size in zip(info["texturePayloads"], info["textureSizes"]):
        assert payload["format"] in ("BC7", "DXT1", "DXT5", "ETC2_RGB", "ETC2_RGBA8", "ASTC_4x4", "ASTC_6x6")
        block = 6 if payload["format"] == "ASTC_6x6" else 4
        block_bytes = 8 if payload["format"] in ("DXT1", "ETC2_RGB") else 16
        width, height = payload["width"], payload["height"]
        expected = 0
        for _ in range(payload["mipCount"]):
            expected += ((width + block - 1) // block) * ((height + block - 1) // block) * block_bytes
            width = max(1, width // 2); height = max(1, height // 2)
        assert expected == size and size <= 128 * 1024 * 1024
references = []
for key, raw in zip(info["materials"], resources[0]):
    material = json.loads(raw)
    assert material["version"] == 3 and material["id"] == key
    for prop in material["properties"]:
        if prop["type"] == 4 and prop.get("textureId"):
            assert prop["textureId"] in info["textures"]
            references.append(prop["textureId"])
vertices = 0
for raw in resources[1]:
    mesh = json.loads(raw)
    assert all(not key or key in info["materials"] for key in mesh["materialIds"])
    assert sum(mesh["indicesCounts"]) == len(mesh["indices"])
    assert all(0 <= index < mesh["vertexCount"] for index in mesh["indices"])
    vertices += mesh["vertexCount"]
playlist = lambda name: [json.loads(line) for line in (root / name).read_text(encoding="utf-8-sig").splitlines() if line.strip()]
chunks = playlist(info["streamInfo"])
audio = playlist(info["audioInfo"])
assert all("sequence" not in row for row in audio), "Audio sequence was not removed"
for row in audio: check_keys(row)
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
print(json.dumps(dict(protocol=info["protocolVersion"], materials=len(info["materials"]), textures=len(info["textures"]),
    meshes=len(info["meshes"]), vertices=vertices, texturePropertyReferences=len(references),
    chunks=len(chunks), frames=frames, audioSegments=len(audio), meshEndSeconds=last_pts/1e7,
    audioEndSeconds=audio[-1]["endTicks"]/1e7, initialBytes=initial_bytes), indent=2))
