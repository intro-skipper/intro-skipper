#!/usr/bin/env python3
"""Dump per-keyframe FFmpeg feature signals for every corpus clip.

For each clip we run ONE keyframe-only decode (`-skip_frame nokey`) with a split
filter graph that emits, per keyframe:

  pblack  : lavfi.blackframe.pblack           (% of frame below luma threshold)
  entY/U/V: lavfi.entropy.normalized_entropy  (0..1 histogram entropy per plane)
  yavg    : lavfi.signalstats.YAVG            (avg luma 0..255)
  satavg  : lavfi.signalstats.SATAVG          (avg saturation -> colourfulness)
  hueavg  : lavfi.signalstats.HUEAVG
  scd     : lavfi.scd.score                   (scene-change score 0..100)
  edge    : YAVG of edgedetect output         (edge/text density proxy 0..255)

Output: corpus/signals/<id>.csv  with one row per keyframe.

These CSVs are the ONLY input prototypes are allowed to consume. Everything here
is emittable by stock FFmpeg, so any algorithm that works on these columns ports
directly to the C# plugin (which already shells out to FFmpeg).
"""
import csv
import glob
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
CLIPS = os.path.join(ROOT, "corpus", "clips")
SIGNALS = os.path.join(ROOT, "corpus", "signals")
THRESHOLD = 32  # luma threshold for blackframe/edge, matches plugin default ballpark

BLOCK_RE = re.compile(r"frame:(\d+)\s+pts:(-?\d+)\s+pts_time:([-\d.]+)")
KV_RE = re.compile(r"(lavfi\.[\w.]+)=([-\d.]+)")


def parse_metadata(path):
    """Parse a metadata=print file into a list of (pts_time, {key: val})."""
    blocks = []
    cur = None
    with open(path) as f:
        for line in f:
            m = BLOCK_RE.search(line)
            if m:
                if cur is not None:
                    blocks.append(cur)
                cur = (float(m.group(3)), {})
                continue
            kv = KV_RE.search(line)
            if kv and cur is not None:
                cur[1][kv.group(1)] = float(kv.group(2))
    if cur is not None:
        blocks.append(cur)
    return blocks


def dump_clip(clip):
    cid = os.path.splitext(os.path.basename(clip))[0]
    main_txt = f"/tmp/_sig_main_{cid}.txt"
    edge_txt = f"/tmp/_sig_edge_{cid}.txt"
    for p in (main_txt, edge_txt):
        if os.path.exists(p):
            os.remove(p)
    fc = (
        f"[0:v]split=2[m][e];"
        f"[m]blackframe=amount=0:threshold={THRESHOLD},entropy,signalstats,scdet=t=0,"
        f"metadata=print:file={main_txt}[mo];"
        f"[e]edgedetect,signalstats,metadata=print:file={edge_txt}[eo];[eo]nullsink"
    )
    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-skip_frame", "nokey",
           "-i", clip, "-an", "-sn", "-dn", "-filter_complex", fc, "-map", "[mo]", "-f", "null", "-"]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        sys.stderr.write(r.stderr[-1000:])
        raise SystemExit(f"dump failed for {cid}")

    main = parse_metadata(main_txt)
    edge = parse_metadata(edge_txt)
    edge_by_t = {round(t, 3): kv.get("lavfi.signalstats.YAVG", "") for t, kv in edge}

    out = os.path.join(SIGNALS, f"{cid}.csv")
    with open(out, "w", newline="") as f:
        wr = csv.writer(f)
        wr.writerow(["t", "pblack", "entY", "entU", "entV", "yavg", "satavg", "hueavg", "scd", "edge"])
        for t, kv in main:
            wr.writerow([
                round(t, 3),
                kv.get("lavfi.blackframe.pblack", 0),
                kv.get("lavfi.entropy.normalized_entropy.normal.Y", ""),
                kv.get("lavfi.entropy.normalized_entropy.normal.U", ""),
                kv.get("lavfi.entropy.normalized_entropy.normal.V", ""),
                kv.get("lavfi.signalstats.YAVG", ""),
                kv.get("lavfi.signalstats.SATAVG", ""),
                kv.get("lavfi.signalstats.HUEAVG", ""),
                kv.get("lavfi.scd.score", 0),
                edge_by_t.get(round(t, 3), ""),
            ])
    os.remove(main_txt)
    os.remove(edge_txt)
    return cid, len(main)


def main():
    os.makedirs(SIGNALS, exist_ok=True)
    clips = sorted(glob.glob(os.path.join(CLIPS, "*.mp4")))
    if not clips:
        raise SystemExit("no clips; run generate_corpus.py first")
    for clip in clips:
        cid, n = dump_clip(clip)
        print(f"  dumped {cid:26s} {n:4d} keyframes")
    print(f"\nDumped {len(clips)} signal CSVs -> {SIGNALS}")


if __name__ == "__main__":
    main()
