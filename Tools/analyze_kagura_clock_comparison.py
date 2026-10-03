#!/usr/bin/env python3
"""Compare KAGURA Time, Playable, DSP and independent monotonic/PCM clocks.

NumPy only. These measurements stop at the Unity listener, not the speaker.
Initial offsets are retained separately from changes and rate differences.
"""
import argparse
import csv
import json
from pathlib import Path
import numpy as np


def read_rows(path):
    with path.open() as stream:
        return list(csv.DictReader(stream))


def describe(values):
    values = np.asarray(values)
    values = values[np.isfinite(values)]
    if not len(values):
        return {'samples': 0}
    return dict(zip(['min', 'p05', 'median', 'p95', 'max'],
                    map(float, np.percentile(values, [0, 5, 50, 95, 100]))))


def compare_clocks(elapsed, difference, mask, bin_seconds=30):
    valid = mask & np.isfinite(difference) & np.isfinite(elapsed)
    bins = []
    for start in np.arange(0, np.max(elapsed[valid]), bin_seconds):
        selected = valid & (elapsed >= max(1, start)) & (elapsed < start + bin_seconds)
        if np.any(selected):
            bins.append({'fromSeconds': float(start), 'time': float(np.median(elapsed[selected])),
                         'offsetMs': float(np.median(difference[selected]) * 1000),
                         'samples': int(np.count_nonzero(selected))})
    x = np.array([b['time'] for b in bins])
    y = np.array([b['offsetMs'] for b in bins])
    slope, intercept = np.polyfit(x, y, 1) if len(bins) > 1 else (0, y[0])
    residual = difference[valid] * 1000 - (intercept + slope * elapsed[valid])
    return {'initialMedianOffsetMs': bins[0]['offsetMs'],
            'firstToLastMedianChangeMs': float(y[-1] - y[0]),
            'fittedRateDifferencePpm': float(slope * 1000),
            'rawOffsetMs': describe(difference[valid] * 1000),
            'detrendedJitterMs': describe(residual), 'bins': bins}


def match_pcm(listener, reference, guess, radius):
    reference = reference.astype(float)
    reference -= reference.mean()
    reference_power = np.dot(reference, reference)
    if reference_power < 1e-10:
        raise ValueError('Silent reference cannot establish sync.')
    left = max(0, guess - radius)
    right = min(len(listener), guess + radius + len(reference))
    search = listener[left:right].astype(float)
    n = len(reference)
    if len(search) < n:
        raise ValueError('Reference falls outside the captured PCM window.')
    fft_size = 1 << ((len(search) + n - 2).bit_length())
    dots = np.fft.irfft(np.fft.rfft(search, fft_size) * np.fft.rfft(reference[::-1], fft_size), fft_size)[n - 1:len(search)]
    sums = np.r_[0, np.cumsum(search)]
    squares = np.r_[0, np.cumsum(search * search)]
    variance = squares[n:] - squares[:-n] - (sums[n:] - sums[:-n]) ** 2 / n
    correlations = dots / np.sqrt(np.maximum(variance, 1e-30) * reference_power)
    lag = int(np.argmax(correlations))
    return left + lag, float(correlations[lag])


def analyze(directory):
    config = json.loads((directory / 'configuration.json').read_text())
    rows = read_rows(directory / 'clock.csv')
    fields = ['frame', 'dspBefore', 'dspAfter', 'monotonic', 'realtime', 'time', 'unscaledTime',
              'delta', 'unscaledDelta', 'sumDelta', 'sumDeltaFloat', 'sumUnscaled', 'timeScale',
              'director', 'audioPlayable', 'bodyPlayable']
    clocks = {field: np.array([float(row[field]) for row in rows]) for field in fields}
    timeline = clocks['director']
    playing = np.array([row['state'] == 'Playing' for row in rows])
    valid = playing & (timeline > 1) & (timeline < config['duration'] - 1)
    pairs = {field + 'MinusDirector': compare_clocks(timeline, clocks[field] - timeline, valid)
             for field in ['dspBefore', 'monotonic', 'realtime', 'time', 'unscaledTime', 'sumDelta', 'sumDeltaFloat', 'sumUnscaled']}
    pairs['sumDeltaMinusTime'] = compare_clocks(timeline, clocks['sumDelta'] - clocks['time'], valid)
    pairs['floatMinusDoubleAccumulation'] = compare_clocks(timeline, clocks['sumDeltaFloat'] - clocks['sumDelta'], valid)
    pairs['realtimeMinusMonotonic'] = compare_clocks(timeline, clocks['realtime'] - clocks['monotonic'], valid)
    active = valid & (timeline > config['audioStart'] + 0.5) & (timeline < config['audioStart'] + config['audioDuration'] - 0.5)
    blocks = read_rows(directory / 'audio-blocks.csv')
    dsp = np.array([float(b['dsp']) for b in blocks])
    mono = np.array([float(b['monotonic']) for b in blocks])
    sample = np.array([int(b['sample']) for b in blocks])
    count = np.array([int(b['count']) for b in blocks])
    channels = np.array([int(b['channels']) for b in blocks])
    rate = config['outputFrequency']
    block_elapsed = dsp - dsp[0]
    block_duration = count / channels / rate
    clock_mask = (block_elapsed > 1) & (block_elapsed < config['duration'] - 1)
    callback_clock = compare_clocks(block_elapsed, mono - dsp, clock_mask)
    early_epoch = np.median((clocks['dspBefore'] - timeline)[playing & (timeline > 1) & (timeline < 20)])
    matches = []
    for song_time in config['referenceTimes']:
        expected = early_epoch + config['audioStart'] + song_time
        chosen = None
        for w in range(3):
            indices = np.array([i for i, b in enumerate(blocks) if int(b['window']) == w])
            if len(indices) and dsp[indices[0]] <= expected <= dsp[indices[-1]]:
                chosen = (w, indices)
                break
        if chosen is None:
            raise ValueError(f'No captured window for song time {song_time}.')
        w, indices = chosen
        channel_count = int(channels[indices[0]])
        listener = np.fromfile(directory / f'listener_{w}.f32', dtype='<f4').reshape(-1, channel_count).mean(axis=1)
        reference = np.fromfile(directory / f'reference_{song_time:g}.f32', dtype='<f4').reshape(-1, config['referenceChannels']).mean(axis=1)
        if config['referenceFrequency'] != rate:
            positions = np.arange(int(len(reference) * rate / config['referenceFrequency'])) * config['referenceFrequency'] / rate
            reference = np.interp(positions, np.arange(len(reference)), reference)
        guess = int((expected - dsp[indices[0]]) * rate)
        frame, correlation = match_pcm(listener, reference, guess, int(0.4 * rate))
        offsets = np.array([int(blocks[i]['offset']) // channel_count for i in indices])
        local_block = np.searchsorted(offsets, frame, side='right') - 1
        block = indices[local_block]
        inside = frame - offsets[local_block]
        actual_dsp = dsp[block] + inside / rate
        actual_monotonic = mono[block] + inside / rate
        actual_sample = sample[block] + inside
        mapped_timeline = np.interp(actual_monotonic, clocks['monotonic'], timeline)
        matches.append({'songTime': song_time, 'correlation': correlation,
                        'listenerDsp': float(actual_dsp), 'globalSample': int(actual_sample),
                        'sampleEpochSeconds': float(actual_sample / rate - song_time),
                        'offsetFromInitialTimelineEpochMs': float((actual_dsp - expected) * 1000),
                        'callbackMappedTimelineOffsetMs': float((mapped_timeline - config['audioStart'] - song_time) * 1000)})
    epochs = np.array([m['sampleEpochSeconds'] for m in matches])
    phases = read_rows(directory / 'phases.csv')
    late = {r['frame']: r for r in phases if r['phase'] == 'LateUpdate'}
    eof = [r for r in phases if r['phase'] == 'EndOfFrame' and r['frame'] in late]
    phase_delay = [(float(r['realtime']) - float(late[r['frame']]['realtime'])) * 1000 for r in eof]
    phase_director = [(float(r['director']) - float(late[r['frame']]['director'])) * 1000 for r in eof]
    hitch = None
    if config.get('controlledHitch'):
        elapsed = clocks['monotonic'] - config['startMonotonic']
        pre = (elapsed > 15) & (elapsed < 19)
        post = (elapsed > 23) & (elapsed < 38)
        near_hitch = (elapsed > 15) & (elapsed < 25)
        hitch = {}
        for name in ['director', 'time', 'unscaledTime', 'sumDelta', 'sumUnscaled', 'realtime']:
            difference = clocks[name] - clocks['monotonic']
            hitch[name + 'MinusMonotonicChangeMs'] = float((np.median(difference[post]) - np.median(difference[pre])) * 1000)
        hitch['maxUnscaledDeltaMs'] = float(np.max(clocks['unscaledDelta'][near_hitch]) * 1000)
        hitch['maxScaledDeltaMs'] = float(np.max(clocks['delta'][near_hitch]) * 1000)
        # A permanent step from a clipped frame is not a clock rate error.
        # Do not present a linear fit across that step as ppm drift or jitter.
        for comparison in pairs.values():
            comparison['fittedRateDifferencePpm'] = None
            comparison['detrendedJitterMs'] = None
            comparison['rateFitApplicable'] = False
    return {
        'complete': (directory / 'complete.txt').exists(), 'configuration': config,
        'clockSamples': len(rows), 'maxTimeline': float(timeline.max()),
        'clockComparisons': pairs,
        'frameDeltaMs': describe(clocks['unscaledDelta'][valid] * 1000),
        'scaledDeltaMs': describe(clocks['delta'][valid] * 1000),
        'controlledHitch': hitch,
        'dspReadBracketMaxMs': float(np.max(clocks['dspAfter'] - clocks['dspBefore']) * 1000),
        'timeScaleRange': [float(clocks['timeScale'].min()), float(clocks['timeScale'].max())],
        'maxAudioPlayableDifferenceMs': float(np.max(np.abs(clocks['audioPlayable'][active] - (timeline[active] - config['audioStart']))) * 1000),
        'maxBodyPlayableDifferenceMs': float(np.max(np.abs(clocks['bodyPlayable'][active] - timeline[active])) * 1000),
        'dspBlockMs': config['dspBufferFrames'] / rate * 1000,
        'audioBlocks': len(blocks), 'audioFrames': int(sample[-1] + count[-1] // channels[-1]),
        'audioCallbackMonotonicMinusDsp': callback_clock,
        'maxDspBlockContinuityErrorMs': float(np.max(np.abs(np.diff(dsp) - block_duration[:-1])) * 1000),
        'maxSampleBlockContinuityErrorFrames': int(np.max(np.abs(np.diff(sample) - count[:-1] // channels[:-1]))),
        'maxDspSampleClockDifferenceChangeMs': float(np.ptp(dsp - sample / rate) * 1000),
        'pcmMatches': matches, 'pcmSampleEpochRangeMs': float(np.ptp(epochs) * 1000),
        'pcmCorrelationVerified': all(m['correlation'] >= 0.95 for m in matches),
        'endOfFrameDelayMs': describe(phase_delay), 'endOfFrameDirectorAdvanceMs': describe(phase_director),
        'limitations': 'Editor clock measurement. Sender encoding was not active; capture PTS/audio origins and ffmpeg are not verified. Playable values share a graph. Main-thread DSP samples are block-quantized. Listener callbacks precede hardware speaker output; display and speaker latency are not measured. Small fitted rate differences do not establish a long-term drift bound.'
    }


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    args = parser.parse_args()
    result = analyze(args.directory)
    (args.directory / 'analysis.json').write_text(json.dumps(result, indent=2) + '\n')
    concise = {key: result[key] for key in ['complete', 'clockSamples', 'maxTimeline', 'frameDeltaMs', 'audioBlocks',
        'maxDspBlockContinuityErrorMs', 'maxSampleBlockContinuityErrorFrames', 'pcmMatches', 'pcmSampleEpochRangeMs', 'endOfFrameDelayMs']}
    concise['clockChanges'] = {key: {k: v[k] for k in ['initialMedianOffsetMs', 'firstToLastMedianChangeMs', 'fittedRateDifferencePpm', 'detrendedJitterMs']}
                              for key, v in result['clockComparisons'].items()}
    print(json.dumps(concise, indent=2))
