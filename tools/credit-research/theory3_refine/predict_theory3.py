#!/usr/bin/env python3
"""Theory-3 prototype: black-frame path first, entropy/saturation fallback second.

Mirrors the intended C# change so thresholds can be explored on the signal CSVs
before porting. The OFFICIAL numbers come from the real analyzer via the runner;
this script only decides which non-black clips a fallback can rescue and whether
the no-credits / dark-scene discipline holds.

Fallback runs ONLY when the black-frame path yields nothing (same gating the C#
analyzer will use), so the dark_noncredit_trap is shielded by its real black
credits exactly like in production.

Usage: predict_theory3.py <variant> <out.csv>
  variant: black | card | card_over
"""
import csv
import glob
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SIGNALS = os.path.join(ROOT, "corpus", "signals")

MIN_PERCENT = 85
MIN_DURATION = 15
MAX_GAP = 20.0
MIN_DENSITY = 0.50

# Fallback thresholds (ported to C# as named constants).
ENT_CARD_MAX = 0.35      # uniform-card background: entropy well below busy content (~0.53)
OVER_YAVG_MAX = 75.0     # darkened montage luma ceiling (content luma ~122)
OVER_SAT_MAX = 75.0      # desaturated montage ceiling (content sat ~108)
OVER_ENT_MIN = 0.35      # over-content is NOT a card (distinguishes from card branch)


def read_signals(path):
    rows = []
    with open(path) as f:
        for r in csv.DictReader(f):
            rows.append(dict(
                t=float(r["t"]),
                pblack=float(r["pblack"] or 0),
                entY=float(r["entY"] or 0),
                yavg=float(r["yavg"] or 0),
                satavg=float(r["satavg"] or 0),
            ))
    return rows


def in_run_gap(rows):
    times = [r["t"] for r in rows]
    gaps = sorted(b - a for a, b in zip(times, times[1:]) if b > a)
    return min(MAX_GAP, gaps[len(gaps) // 2] * 5.0) if gaps else MAX_GAP


def find_runs(rows, predicate, gap):
    runs, start, last = [], None, None
    for r in rows:
        if not predicate(r):
            continue
        if start is None:
            start, last = r["t"], r["t"]
            continue
        if r["t"] - last > gap:
            runs.append((start, last))
            start = r["t"]
        last = r["t"]
    if start is not None:
        runs.append((start, last))
    return runs


def normalize_minimum(rows):
    pblacks = sorted(r["pblack"] for r in rows)
    floor = min(pblacks[int(len(pblacks) * 0.01)], 30)
    return (MIN_PERCENT * (100 - floor) / 100) + floor


def detect_black(rows):
    if not rows:
        return None
    minimum = normalize_minimum(rows)
    gap = in_run_gap(rows)
    runs = find_runs(rows, lambda r: r["pblack"] >= minimum, gap)
    best = None
    for s, e in runs:
        seg = [r for r in rows if s <= r["t"] <= e]
        density = sum(1 for r in seg if r["pblack"] >= minimum) / len(seg)
        if density >= MIN_DENSITY and (e - s) >= MIN_DURATION:
            best = s
    return best


def detect_fallback(rows, allow_over):
    """Entropy card fallback (+ optional darkened-montage branch). Latest run wins."""
    gap = in_run_gap(rows)

    def is_card(r):
        return r["entY"] < ENT_CARD_MAX

    def is_over(r):
        return (r["entY"] >= OVER_ENT_MIN and r["yavg"] < OVER_YAVG_MAX
                and r["satavg"] < OVER_SAT_MAX)

    def predicate(r):
        return is_card(r) or (allow_over and is_over(r))

    runs = find_runs(rows, predicate, gap)
    best = None
    for s, e in runs:
        if (e - s) >= MIN_DURATION:
            best = s  # latest qualifying run
    return best


def detect(rows, variant):
    black = detect_black(rows)
    if black is not None or variant == "black":
        return black
    return detect_fallback(rows, allow_over=(variant == "card_over"))


def main():
    variant = sys.argv[1] if len(sys.argv) > 1 else "card"
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        ROOT, "theory3_refine", "predictions", f"theory3_{variant}.csv")
    with open(out, "w", newline="") as f:
        wr = csv.writer(f)
        wr.writerow(["id", "predicted_start"])
        for path in sorted(glob.glob(os.path.join(SIGNALS, "*.csv"))):
            cid = os.path.splitext(os.path.basename(path))[0]
            pred = detect(read_signals(path), variant)
            wr.writerow([cid, "" if pred is None else round(pred, 3)])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
