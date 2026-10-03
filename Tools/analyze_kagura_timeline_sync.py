#!/usr/bin/env python3
"""Analyze Editor-only KAGURA Timeline clock and listener PCM diagnostics.

Requires NumPy. Listener DSP block timestamps do not measure speaker latency.
"""

import argparse
import csv
import json
from pathlib import Path

import numpy as np


def read_csv(path):
    with path.open() as stream:
        return list(csv.DictReader(stream))


def analyze(directory):
    config = json.loads((directory / "configuration.json").read_text())
    rows = read_csv(directory / "clock.csv")
    timeline = np.array([float(row["director"]) for row in rows])
    dsp = np.array([float(row["dsp"]) for row in rows])
    audio = np.array([float(row["audioPlayable"]) for row in rows])
    body = np.array([float(row["bodyPlayable"]) for row in rows])
    start = config["audioStart"]
    playing = np.array([row["state"] == "Playing" for row in rows])
    max_timeline = float(np.max(timeline))
    # Exclude preparation and the held pose/audio after each clip's end.
    active = playing & (timeline > start + 0.5) & (timeline < start + config["audioDuration"] - 0.5)
    epoch = dsp - timeline
    early_epoch = float(np.median(epoch[(timeline > 1) & (timeline < 18)]))
    bins = []
    for left in range(0, int(min(max_timeline, config["duration"])), 30):
        selected = playing & (timeline > max(1, left)) & (timeline < left + 30)
        if np.any(selected):
            bins.append({"timelineFrom": left,
                         "medianDspMinusTimeline": float(np.median(epoch[selected]))})

    blocks = read_csv(directory / "audio-blocks.csv")
    channels = int(blocks[0]["channels"])
    if any(int(block["channels"]) != channels for block in blocks):
        raise ValueError("Audio channel count changed during capture.")
    rate = config["outputFrequency"]
    listener = np.fromfile(directory / "listener.f32", dtype="<f4").reshape(-1, channels).mean(axis=1)
    reference = np.fromfile(directory / "reference.f32", dtype="<f4").reshape(-1, config["referenceChannels"]).mean(axis=1)
    if config["referenceFrequency"] != rate:
        positions = np.arange(int(len(reference) * rate / config["referenceFrequency"])) * config["referenceFrequency"] / rate
        reference = np.interp(positions, np.arange(len(reference)), reference)
    offsets = np.array([int(block["offset"]) // channels for block in blocks])
    block_times = np.array([float(block["dsp"]) for block in blocks])
    matches = []
    for song_time in [4, 8, 10]:
        window = reference[int(song_time * rate):int((song_time + 0.3) * rate)].astype(float)
        window -= window.mean()
        if np.dot(window, window) < 1e-10:
            raise ValueError("Reference window has no usable audio signal.")
        guess = int((early_epoch + start + song_time - block_times[0]) * rate)
        left = max(0, guess - int(0.3 * rate))
        right = min(len(listener), guess + int(0.3 * rate) + len(window))
        search = listener[left:right].astype(float)
        fft_size = 1 << ((len(search) + len(window) - 2).bit_length())
        dots = np.fft.irfft(np.fft.rfft(search, fft_size) * np.fft.rfft(window[::-1], fft_size), fft_size)[len(window) - 1:len(search)]
        sums = np.concatenate(([0.0], np.cumsum(search * search)))
        energy = sums[len(window):] - sums[:-len(window)]
        correlations = dots / np.sqrt(np.maximum(energy, 1e-30) * np.dot(window, window))
        lag = int(np.argmax(correlations))
        frame = left + lag
        block = np.searchsorted(offsets, frame, side="right") - 1
        actual_dsp = block_times[block] + (frame - offsets[block]) / rate
        matches.append({"songTime": song_time, "correlation": float(correlations[lag]),
                        "listenerDsp": float(actual_dsp),
                        "estimatedTimelineDsp": early_epoch + start + song_time,
                        "estimatedOffsetMs": float((actual_dsp - early_epoch - start - song_time) * 1000)})

    return {
        "complete": (directory / "complete.txt").exists(),
        "configuration": config,
        "clockSamples": len(rows),
        "lastTimeline": float(timeline[-1]),
        "maxTimeline": max_timeline,
        "lastDirectorState": rows[-1]["state"],
        "maxAudioPlayableDifferenceMs": float(np.max(np.abs(audio[active] - (timeline[active] - start))) * 1000),
        "maxBodyPlayableDifferenceMs": float(np.max(np.abs(body[active] - timeline[active])) * 1000),
        "dspBlockMs": config["dspBufferFrames"] / rate * 1000,
        "dspEpochBins": bins,
        "firstToLastMedianEpochChangeMs": (bins[-1]["medianDspMinusTimeline"] - bins[0]["medianDspMinusTimeline"]) * 1000,
        "listenerChannels": channels,
        "audioBlocks": len(blocks),
        "maxAudioBlockContinuityErrorMs": float(np.max(np.abs(np.diff(block_times) - np.array([int(block["count"]) / channels / rate for block in blocks[:-1]]))) * 1000),
        "pcmMatches": matches,
        "limitations": "AudioSettings.dspTime is block-quantized. PCM offsets are estimates within that block timing; hardware speaker/display latency is not measured. PCM matching covers only the captured first 18 seconds."
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", nargs="?", type=Path, default=Path("Logs/KaguraTimelineSync"))
    args = parser.parse_args()
    result = analyze(args.directory)
    serialized = json.dumps(result, indent=2) + "\n"
    (args.directory / "analysis.json").write_text(serialized)
    print(serialized)
