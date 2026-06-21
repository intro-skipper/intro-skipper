#!/usr/bin/env python3
"""Characterize free per-keyframe signals: credit region vs content region.

For each labeled clip, split keyframes into content (t < credit_start) and
credit (t >= credit_start) and print median signal values for both, so we can
see which FFmpeg-emittable signal separates non-black credits from content
without touching pblack.
"""
import csv
import os
import statistics as st

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SIGNALS = os.path.join(ROOT, "corpus", "signals")
LABELS = os.path.join(ROOT, "corpus", "labels.csv")

COLS = ["pblack", "entY", "entU", "entV", "yavg", "satavg", "hueavg", "scd", "edge"]


def load_labels():
    out = {}
    with open(LABELS) as f:
        for r in csv.DictReader(f):
            out[r["id"]] = r
    return out


def read_signals(path):
    rows = []
    with open(path) as f:
        for r in csv.DictReader(f):
            rows.append({k: float(r[k] or 0) for k in ["t"] + COLS})
    return rows


def med(rows, col):
    vals = [r[col] for r in rows]
    return st.median(vals) if vals else float("nan")


def main():
    labels = load_labels()
    print(f"{'clip':22s} {'arch':16s} {'reg':4s} {'n':>3s} "
          f"{'pblk':>5s} {'entY':>5s} {'entU':>5s} {'entV':>5s} "
          f"{'yavg':>6s} {'satv':>6s} {'scd':>5s} {'edge':>6s}")
    for cid, lab in sorted(labels.items(), key=lambda kv: kv[1]["archetype"]):
        path = os.path.join(SIGNALS, f"{cid}.csv")
        if not os.path.exists(path):
            continue
        rows = read_signals(path)
        gt = float(lab["credit_start_s"]) if lab["credit_start_s"] else None
        if gt is None:
            groups = [("all", rows)]
        else:
            groups = [
                ("cont", [r for r in rows if r["t"] < gt - 0.5]),
                ("cred", [r for r in rows if r["t"] >= gt]),
            ]
        for name, g in groups:
            if not g:
                continue
            print(f"{cid:22s} {lab['archetype']:16s} {name:4s} {len(g):3d} "
                  f"{med(g,'pblack'):5.0f} {med(g,'entY'):5.2f} {med(g,'entU'):5.2f} "
                  f"{med(g,'entV'):5.2f} {med(g,'yavg'):6.1f} {med(g,'satavg'):6.1f} "
                  f"{med(g,'scd'):5.1f} {med(g,'edge'):6.2f}")
        print()


if __name__ == "__main__":
    main()
