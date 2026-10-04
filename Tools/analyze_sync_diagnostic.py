#!/usr/bin/env python3
"""Compare recorded stopwatch-hand crossings with AAC beeps."""

import argparse
import array
import gzip
import json
import math
import statistics
import struct
import subprocess
import tempfile
from pathlib import Path


def mesh_samples(channel: Path) -> list[tuple[float, float, float]]:
    metadata = json.loads((channel / "stream.json").read_text())
    half_package = metadata["packageSize"] // 2
    tile_scale = metadata["containerSize"] / half_package
    sub_tile_scale = tile_scale / 32
    samples = []
    tip = None
    tip_delta_slot = None
    chunks = [channel / json.loads(line)["video"]
              for line in (channel / "stream.stmj").read_text().splitlines() if line]
    for chunk in chunks:
        data = gzip.decompress(chunk.read_bytes())
        count = struct.unpack_from("<i", data)[0]
        offset = (count + 1) * 4
        for i in range(count):
            size = struct.unpack_from("<i", data, (i + 1) * 4)[0]
            frame = memoryview(data)[offset : offset + size]
            if len(frame) < 29 or frame[8] < 2:
                raise ValueError(f"{chunk}: frames must contain presentation timestamps")
            if abs(struct.unpack_from("<f", frame, 13)[0]) > 0.0001:
                raise ValueError(f"{chunk}: clock root moves vertically")
            if frame[0] == 0x0F:
                package_count = frame[5] | frame[6] << 8 | frame[7] << 16
                package_offset = 29
                packed_slot = 0
                tip_delta_slot = None
                for _ in range(package_count):
                    tile_x, tile_y, _, count_low, count_mid, count_high = frame[
                        package_offset : package_offset + 6]
                    vertex_count = count_low | count_mid << 8 | count_high << 16
                    package_offset += 6
                    for vertex in range(vertex_count):
                        vertex_offset = package_offset + vertex * 5
                        vertex_index, mesh_index, compressed = struct.unpack_from(
                            "<HBH", frame, vertex_offset)
                        if mesh_index == 0 and vertex_index == 3:
                            tip = (
                                (tile_x - half_package) * tile_scale
                                + (compressed & 0x1F) * sub_tile_scale,
                                (tile_y - half_package) * tile_scale
                                + ((compressed >> 5) & 0x1F) * sub_tile_scale,
                            )
                            tip_delta_slot = packed_slot
                        packed_slot += 1
                    package_offset += vertex_count * 5
            elif frame[0] == 0x0E and tip is not None and tip_delta_slot is not None:
                delta_offset = 29 + tip_delta_slot * 3
                dx, dy = (value - 128 for value in frame[delta_offset : delta_offset + 2])
                tip = tuple(value + (1 if delta >= 0 else -1) * delta * delta / 16384
                            for value, delta in zip(tip, (dx, dy)))
            else:
                raise ValueError(f"{chunk}: invalid frame or missing stopwatch hand")

            timestamp = struct.unpack_from("<q", frame, 21)[0] / metadata["timebaseHz"]
            if samples and timestamp <= samples[-1][0]:
                raise ValueError(f"{chunk}: presentation timestamps must increase")
            samples.append((timestamp, *tip))
            offset += size
    return samples


def mesh_beats(samples: list[tuple[float, float, float]]) -> list[float]:
    beats = []
    for previous, current in zip(samples, samples[1:]):
        previous_time, previous_x, previous_y = previous
        current_time, current_x, current_y = current
        # Use the same linear vertex interpolation as the Receiver. The initial
        # parked hand is not a crossing, so exclude the first audio beep later.
        if previous_x < 0 <= current_x and previous_y > 1.3 and current_y > 1.3:
            interpolation = -previous_x / (current_x - previous_x)
            beats.append(previous_time + (current_time - previous_time) * interpolation)
    return beats


def hand_angle_at_time(samples: list[tuple[float, float, float]], timestamp: float) -> float:
    for previous, current in zip(samples, samples[1:]):
        if previous[0] <= timestamp <= current[0]:
            interpolation = (timestamp - previous[0]) / (current[0] - previous[0])
            x = previous[1] + (current[1] - previous[1]) * interpolation
            y = previous[2] + (current[2] - previous[2]) * interpolation
            return math.degrees(math.atan2(x, y))
    raise ValueError(f"No mesh poses bracket audio onset {timestamp:.6f} s")


def audio_beats(channel: Path, ffmpeg: str) -> list[float]:
    segments = [channel / json.loads(line)["audio"]
                for line in (channel / "stream.stma").read_text().splitlines() if line]
    if not segments:
        raise ValueError("No completed audio segments found")
    with tempfile.TemporaryDirectory() as directory:
        combined = Path(directory) / "audio.mp4"
        with combined.open("wb") as output:
            output.write((channel / "audio-init.mp4").read_bytes())
            for segment in segments:
                output.write(segment.read_bytes())
        result = subprocess.run(
            [ffmpeg, "-v", "error", "-i", str(combined), "-ac", "1", "-ar", "48000",
             "-f", "f32le", "pipe:1"],
            check=True,
            stdout=subprocess.PIPE,
        )
    samples = array.array("f")
    samples.frombytes(result.stdout)
    if struct.pack("=H", 1) != struct.pack("<H", 1):
        samples.byteswap()
    block = 48  # 1 ms at 48 kHz.
    beats = []
    for start in range(0, len(samples) - block, block):
        rms2 = sum(sample * sample for sample in samples[start : start + block]) / block
        timestamp = start / 48000
        if rms2 > 0.02 * 0.02 and (not beats or timestamp - beats[-1] > 0.5):
            beats.append(timestamp)
    return beats


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--channel", type=Path,
                        default=Path("DevData/channels/channel_sync_mesh"))
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--report", type=Path, help="Save measurements as JSON")
    args = parser.parse_args()
    samples = mesh_samples(args.channel)
    visual = mesh_beats(samples)
    audio = audio_beats(args.channel, args.ffmpeg)
    if len(visual) < 2 or len(audio) < 2:
        raise SystemExit(f"Need at least two beats: mesh={len(visual)}, audio={len(audio)}")
    # The hand stays at 12 before rotation starts; that first beep cannot be
    # timed from a crossing. Require all subsequent crossings/beeps to match.
    compared_audio = audio[1:]
    if len(visual) != len(compared_audio):
        raise SystemExit(f"Event count mismatch: mesh={len(visual)}, "
                         f"audio after parked first beep={len(compared_audio)}")
    offsets = [crossing - beep for crossing, beep in zip(visual, compared_audio)]
    if any(abs(offset) >= 0.5 for offset in offsets):
        raise SystemExit("Crossings do not match their one-second beep sequence")
    angles = [hand_angle_at_time(samples, beep) for beep in compared_audio]
    interval = json.loads((args.channel / "stream.json").read_text())["frameInterval"]
    print(f"mesh crossings={len(visual)}, audio beeps={len(audio)}, compared={len(offsets)} "
          "(first parked beep excluded)")
    print("visual minus audio (ms): " +
          ", ".join(f"{offset * 1000:+.1f}" for offset in offsets[:10]) +
          (" ..." if len(offsets) > 10 else ""))
    print(f"interpolated offset median={statistics.median(offsets) * 1000:+.1f} ms "
          f"(range {min(offsets) * 1000:+.1f} to {max(offsets) * 1000:+.1f} ms)")
    print(f"hand at audio onset median={statistics.median(angles):+.1f} degrees "
          "clockwise from 12 (negative means before 12)")
    print(f"capture frame interval={interval * 1000:.1f} ms; offsets are not reduced modulo it")
    print(f"first-to-last offset change={(offsets[-1] - offsets[0]) * 1000:+.1f} ms "
          f"over {compared_audio[-1] - compared_audio[0]:.1f} s")
    if args.report:
        report = {
            "channel": str(args.channel), "frames": len(samples),
            "firstPtsSeconds": samples[0][0], "lastPtsSeconds": samples[-1][0],
            "maximumFrameGapMs": max(b[0] - a[0] for a, b in zip(samples, samples[1:])) * 1000,
            "crossings": len(visual), "beeps": len(audio),
            "offsetMedianMs": statistics.median(offsets) * 1000,
            "offsetMinimumMs": min(offsets) * 1000, "offsetMaximumMs": max(offsets) * 1000,
            "firstToLastOffsetChangeMs": (offsets[-1] - offsets[0]) * 1000,
            "comparedDurationSeconds": compared_audio[-1] - compared_audio[0],
            "offsetsMs": [offset * 1000 for offset in offsets],
        }
        args.report.write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
