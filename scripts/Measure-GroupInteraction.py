"""GroupInteractionRun의 활성 구간과 프레임/입력/GC를 합친다. 인수: 실행 출력 폴더."""
import csv
import json
import math
import sys
from pathlib import Path


def percentile(values, fraction):
    return sorted(values)[max(0, math.ceil(len(values) * fraction) - 1)] if values else None


def measure(directory):
    report = json.loads((directory / 'report.json').read_text(encoding='utf-8-sig'))
    if report['error']:
        raise ValueError(report['error'])
    with (directory / 'frames.csv').open(encoding='utf-8-sig', newline='') as source:
        frames = list(csv.DictReader(source))
    frequency = report['stopwatchFrequency']
    phases = []
    for phase in report['phases']:
        rows = [row for row in frames if phase['startTicks'] <= int(row['stopwatch_record_ticks']) <= phase['endTicks']]
        samples = phase['samples']
        if len(rows) < 2 or not samples:
            raise ValueError(f"Insufficient active data: {phase['name']}")
        # 첫 프레임 간격은 이전 정적 구간을 포함할 수 있다. 시작→첫 프레임 시간은 별도 보존한다.
        intervals = [float(row['interval_ms']) for row in rows[1:]]
        calls = [(s['endTicks'] - s['executeTicks']) * 1000 / frequency for s in samples]
        queues = [(s['executeTicks'] - s['createdTicks']) * 1000 / frequency for s in samples]
        previews = [s for s in samples if s.get('previewAngle') is not None]
        display = []
        ordered = True
        for row in rows:
            if not row.get('display_present_count'):
                continue
            value = tuple(int(row[key]) for key in ('display_present_count', 'display_refresh_count', 'display_sync_refresh_count', 'display_sync_qpc'))
            if display and any(value[i] < display[-1][i] for i in range(4)):
                ordered = False
            if not display or value[0] != display[-1][0]:
                display.append(value)
        excess = None
        cadence = None
        if ordered and len(display) > 1:
            delta = [display[-1][i] - display[0][i] for i in range(4)]
            if all(v > 0 for v in delta):
                cadence = delta[0] / delta[1] * delta[2] * frequency / delta[3]
                excess = sum(max(0, (b[1] - a[1]) - (b[0] - a[0])) for a, b in zip(display, display[1:]))
        fps = (len(rows) - 1) * frequency / (int(rows[-1]['stopwatch_record_ticks']) - int(rows[0]['stopwatch_record_ticks']))
        phases.append(dict(
            name=phase['name'], iteration=phase['iteration'], warmup=phase['iteration'] == 0, frames=len(rows), fps=fps,
            intervalP95Ms=percentile(intervals, .95), intervalMaxMs=max(intervals),
            overBudgetFrames=sum(v > 1000 / 60 for v in intervals), over20MsFrames=sum(v > 20 for v in intervals),
            strict60Fps='PASS' if fps >= 60 and max(intervals) <= 1000 / 60 else 'FAIL',
            displayCadenceFps=cadence, displayExcessRefreshes=excess,
            firstFrameMs=(int(rows[0]['stopwatch_record_ticks']) - phase['startTicks']) * 1000 / frequency,
            firstPreviewMs=(previews[0]['endTicks'] - phase['startTicks']) * 1000 / frequency if previews else None,
            commandMs=(phase['commandEndTicks'] - phase['startTicks']) * 1000 / frequency,
            inputCount=len(samples), changedSamples=sum(s.get('sceneChanged', False) for s in samples),
            callbackP95Ms=percentile(calls, .95), callbackMaxMs=max(calls), queueP95Ms=percentile(queues, .95), queueMaxMs=max(queues),
            gcPauseMs=phase['gcPauseMs'], allocatedBytes=phase['allocatedBytes'],
            gpuP95Ms=percentile([float(row['gpu_ms']) for row in rows if row['gpu_ms']], .95),
            sceneAgeP95Ms=percentile([float(row['scene_age_ms']) for row in rows], .95)))
    selections = [s['elapsedMs'] for s in report.get('selectionSamples', []) if s['iteration'] > 0]
    result = dict(count=report['count'], adapter=report['GpuStatus'], coreSha256=report['coreSha256'],
                  note=report['note'], budgetMs=1000 / 60, phases=phases,
                  selectionCount=len(selections), selectionMeanMs=sum(selections) / len(selections) if selections else None,
                  selectionP95Ms=percentile(selections, .95), selectionMaxMs=max(selections) if selections else None)
    (directory / 'summary.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    for phase in phases:
        if not phase['warmup']:
            print(f"{phase['iteration']} {phase['name']:18} FPS={phase['fps']:.2f} frame95/max={phase['intervalP95Ms']:.2f}/{phase['intervalMaxMs']:.2f} "
                  f"input95/max={phase['callbackP95Ms']:.2f}/{phase['callbackMaxMs']:.2f} queueMax={phase['queueMaxMs']:.2f} "
                  f"command={phase['commandMs']:.1f} changed={phase['changedSamples']}/{phase['inputCount']} {phase['strict60Fps']}")
    return result


if __name__ == '__main__':
    measure(Path(sys.argv[1]))
