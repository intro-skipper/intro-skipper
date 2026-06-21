#!/usr/bin/env python3
"""Reference predictor: reproduce the current black-frame analyzer from signal CSVs.

This is BOTH a sanity check (does a CSV-only reimplementation match the real C#
analyzer's black-credit detections?) AND the template every theory prototype
should follow:

    read corpus/signals/<id>.csv  ->  emit predictions/<name>.csv (id,predicted_start)

It only uses the `pblack` column (what the production analyzer sees today), so it
is deliberately blind to non-black credits — exactly like the baseline.
"""
import csv
import glob
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SIGNALS = os.path.join(ROOT, "corpus", "signals")
OUT = os.path.join(ROOT, "predictions", "ref_blackframe_csv.csv")

MIN_PERCENT = 85          # BlackFrameMinimumPercentage
MIN_DURATION = 15         # MinimumCreditsDuration
MAX_GAP = 20.0            # MaximumSceneMergeGapSeconds
MIN_DENSITY = 0.50        # DefaultMinimumBlackFrameDensity


def read_signals(path):
    rows = []
    with open(path) as f:
        for r in csv.DictReader(f):
            rows.append((float(r["t"]), float(r["pblack"] or 0)))
    return rows


def normalize_threshold(frames):
    # Mirror CreditsBlackFrameAnalyzer.NormalizeThreshold: percentile floor capped at 30.
    pblacks = sorted(p for _, p in frames)
    floor = min(pblacks[int(len(pblacks) * 0.01)], 30)
    minimum = (MIN_PERCENT * (100 - floor) / 100) + floor
    return minimum


def detect(frames):
    if not frames:
        return None
    minimum = normalize_threshold(frames)
    # group consecutive black keyframes into scenes (gap-based)
    scenes, start, last = [], None, None
    # median keyframe gap to bound in-run gap (approx EstimateMaximumInRunGap)
    times = [t for t, _ in frames]
    gaps = sorted(b - a for a, b in zip(times, times[1:]) if b > a)
    in_run_gap = min(MAX_GAP, gaps[len(gaps)//2] * 5.0) if gaps else MAX_GAP
    for t, p in frames:
        if p < minimum:
            continue
        if start is None:
            start, last = t, t
            continue
        if t - last > in_run_gap:
            scenes.append((start, last))
            start = t
        last = t
    if start is not None:
        scenes.append((start, last))
    # density gate + duration; pick the LATEST qualifying scene
    best = None
    for s, e in scenes:
        seg = [(t, p) for t, p in frames if s <= t <= e]
        if not seg:
            continue
        density = sum(1 for _, p in seg if p >= minimum) / len(seg)
        if density >= MIN_DENSITY and (e - s) >= MIN_DURATION:
            best = s  # latest wins (loop order is ascending)
    return best


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", newline="") as f:
        wr = csv.writer(f)
        wr.writerow(["id", "predicted_start"])
        for path in sorted(glob.glob(os.path.join(SIGNALS, "*.csv"))):
            cid = os.path.splitext(os.path.basename(path))[0]
            pred = detect(read_signals(path))
            wr.writerow([cid, "" if pred is None else round(pred, 3)])
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
