#!/usr/bin/env python3
"""Summarize Sender frame costs and audio callback cadence. Not a speaker test."""
import argparse
import csv
import json
from pathlib import Path
import numpy as np


def describe(values):
    values = np.asarray(values)
    return dict(zip(('min', 'median', 'p95', 'p99', 'max'), map(float, np.percentile(values, [0, 50, 95, 99, 100])))) if len(values) else {}


def analyze(directory):
    config = json.loads((directory / 'configuration.json').read_text())
    rows = list(csv.DictReader((directory / 'frames.csv').open()))
    f = {k: np.array([float(r[k]) for r in rows]) for k in rows[0]}
    mask = f['elapsed'] >= 2  # Exclude restart and initial allocations.
    blocks = list(csv.DictReader((directory / 'audio-blocks.csv').open()))
    b = {k: np.array([float(r[k]) for r in blocks]) for k in blocks[0]}
    audio_mask = (b['receipt'][1:] - b['receipt'][0]) / config['frequency'] >= 2
    intervals = (np.diff(b['receipt']) / config['frequency'])[audio_mask]
    expected = (b['samples'][:-1] / config['sampleRate'])[audio_mask]
    dsp_errors = np.diff(b['dsp'])[audio_mask] - expected
    gc_frames = np.r_[False, np.diff(f['gcCollections']) > 0]
    slow = mask & (f['interval'] > .05)
    marker_fields = ('gcNs', 'mainNs', 'sourceAudioNs', 'recorderAudioNs', 'encodeNs', 'bakeNs', 'readbackNs', 'commitNs', 'flushNs', 'publishNs')
    costs = {k: describe(f[k][mask & (f[k] >= 0)] / 1e6) for k in marker_fields}
    # Costs are recorder totals from the previous frame. Nested marker costs overlap.
    worst = sorted(np.flatnonzero(mask), key=lambda i: f['interval'][i], reverse=True)[:12]
    result = {
        'configuration': config, 'frames': len(rows), 'elapsedSeconds': float(f['elapsed'][-1]),
        'frameIntervalMs': describe(f['interval'][mask] * 1000),
        'framesOver50Ms': int(slow.sum()), 'framesOver25Ms': int((mask & (f['interval'] > .025)).sum()),
        'framesOver25MsWithGcCompletion': int((mask & (f['interval'] > .025) & gc_frames).sum()), 'gcCollections': int(f['gcCollections'][-1] - f['gcCollections'][0]),
        'allocationBytesPerFrame': describe(f['allocatedBytes'][mask]),
        'allocationMBPerSecond': float(f['allocatedBytes'][mask].sum() / np.diff(f['elapsed'])[mask[1:]].sum() / 1e6),
        'markerMs': costs, 'observationDriftMs': describe(f['observationDrift'][mask] * 1000),
        'timelineDriftMs': describe(f['timelineDrift'][mask] * 1000),
        'missedCaptureSlots': int(f['missedSlots'][-1]),
        'clockGenerations': sorted(set(int(v) for v in f['generation'])),
        'audioBlocks': len(blocks), 'audioCallbackIntervalMs': describe(intervals * 1000),
        'audioCallbackIntervalsOver2Blocks': int((intervals > expected * 2).sum()),
        'audioCallbackEarlyIntervals': int((intervals < expected * .5).sum()),
        'audioDspContinuityErrorMs': describe(dsp_errors * 1000),
        'audioDspContinuityViolations': int((np.abs(dsp_errors) > 1.5 / config['sampleRate']).sum()),
        'audioReceiptMinusDspRangeMs': float(np.ptp(b['receipt'] / config['frequency'] - b['dsp']) * 1000),
        'worstFrames': [{
            'elapsed': float(f['elapsed'][i]), 'intervalMs': float(f['interval'][i] * 1000),
            'driftMs': float(f['observationDrift'][i] * 1000), 'gcCompleted': bool(gc_frames[i]),
            'allocatedMB': float(f['allocatedBytes'][i] / 1e6),
            'costMs': {k: float(f[k][i] / 1e6) for k in marker_fields}
        } for i in worst],
        'limitations': 'Profiler marker costs may overlap and cover the previous frame. Callback timing measures the Unity audio graph, not the physical output. Increased Observation Drift is a phase innovation, not an accumulated A/V error or proof of an underrun.'
    }
    (directory / 'analysis.json').write_text(json.dumps(result, indent=2) + '\n')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directories', nargs='+', type=Path)
    args = parser.parse_args()
    for directory in args.directories:
        result = analyze(directory)
        print(json.dumps({'directory': str(directory), **{k: v for k, v in result.items() if k not in ('worstFrames', 'limitations')}}, indent=2))
