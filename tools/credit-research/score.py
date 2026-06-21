#!/usr/bin/env python3
"""Score credit-start predictions against the labeled corpus.

Usage:
    python3 score.py predictions/<name>.csv [--tol 2,5] [--json out.json]

Predictions CSV format (header required):
    id,predicted_start
    color_card,30.1
    no_credits,            <- empty = "no credits predicted"

Metrics:
    * boundary MAE / median abs error over credited clips that were detected
    * hit-rate within each tolerance (|pred-gt| <= tol)
    * miss-rate (credited clip, no prediction)
    * false-positive rate (non-credited clip, prediction emitted)
    * per-archetype breakdown
A single headline number (`overall`) combines hit@tol[0], (1-miss), (1-FP) so
prototypes can be ranked at a glance. Higher is better.
"""
import argparse
import csv
import json
import os
import statistics
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
LABELS = os.path.join(ROOT, "corpus", "labels.csv")


def load_labels():
    out = {}
    with open(LABELS) as f:
        for row in csv.DictReader(f):
            out[row["id"]] = row
    return out


def load_predictions(path):
    out = {}
    with open(path) as f:
        for row in csv.DictReader(f):
            v = (row.get("predicted_start") or "").strip()
            out[row["id"]] = float(v) if v not in ("", "none", "None") else None
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("predictions")
    ap.add_argument("--tol", default="2,5")
    ap.add_argument("--json", default=None)
    args = ap.parse_args()
    tols = [float(x) for x in args.tol.split(",")]

    labels = load_labels()
    preds = load_predictions(args.predictions)

    errors, hits = [], {t: 0 for t in tols}
    credited = misses = fp = tn = 0
    per_arch = {}
    rows = []

    for cid, lab in labels.items():
        has = lab["has_credits"] == "1"
        gt = float(lab["credit_start_s"]) if lab["credit_start_s"] else None
        pred = preds.get(cid, None)
        arch = lab["archetype"]
        pa = per_arch.setdefault(arch, {"n": 0, "hit": 0, "miss": 0, "fp": 0, "err": []})
        pa["n"] += 1
        status = ""
        if has:
            credited += 1
            if pred is None:
                misses += 1
                pa["miss"] += 1
                status = "MISS"
            else:
                err = abs(pred - gt)
                errors.append(err)
                pa["err"].append(err)
                for t in tols:
                    if err <= t:
                        hits[t] += 1
                if err <= tols[0]:
                    pa["hit"] += 1
                status = f"err={err:5.2f}"
        else:
            if pred is None:
                tn += 1
                status = "OK(none)"
            else:
                fp += 1
                pa["fp"] += 1
                status = f"FALSE+@{pred:.1f}"
        rows.append((cid, arch, "" if gt is None else f"{gt:.1f}",
                     "none" if pred is None else f"{pred:.1f}", status))

    non_credited = sum(1 for l in labels.values() if l["has_credits"] != "1")
    hit_rate = {t: hits[t] / credited for t in tols} if credited else {t: 0 for t in tols}
    miss_rate = misses / credited if credited else 0
    fp_rate = fp / non_credited if non_credited else 0
    mae = statistics.mean(errors) if errors else float("nan")
    med = statistics.median(errors) if errors else float("nan")
    overall = (hit_rate[tols[0]] * (1 - miss_rate) * (1 - fp_rate))

    print(f"\n=== {os.path.basename(args.predictions)} ===")
    print(f"{'clip':28s} {'archetype':16s} {'gt':>6s} {'pred':>7s}  status")
    for cid, arch, gt, pred, status in sorted(rows):
        print(f"{cid:28s} {arch:16s} {gt:>6s} {pred:>7s}  {status}")
    print("\n-- per archetype (hit@%.0fs / n, misses, false+) --" % tols[0])
    for arch, pa in sorted(per_arch.items()):
        med_a = f"{statistics.median(pa['err']):.2f}" if pa["err"] else "-"
        print(f"  {arch:16s} hit {pa['hit']}/{pa['n']-pa['fp']}  miss {pa['miss']}  fp {pa['fp']}  medErr {med_a}")
    print("\n-- summary --")
    for t in tols:
        print(f"  hit@{t:g}s     : {hit_rate[t]*100:5.1f}%  ({hits[t]}/{credited})")
    print(f"  miss-rate   : {miss_rate*100:5.1f}%  ({misses}/{credited})")
    print(f"  false-pos   : {fp_rate*100:5.1f}%  ({fp}/{non_credited})")
    print(f"  MAE/median  : {mae:.2f}s / {med:.2f}s  (detected only)")
    print(f"  OVERALL     : {overall*100:5.1f}   [hit@{tols[0]:g} x (1-miss) x (1-fp)]")

    if args.json:
        json.dump(dict(hit_rate=hit_rate, miss_rate=miss_rate, fp_rate=fp_rate,
                       mae=mae, median=med, overall=overall), open(args.json, "w"), indent=2)


if __name__ == "__main__":
    main()
