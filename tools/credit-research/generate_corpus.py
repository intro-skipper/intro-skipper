#!/usr/bin/env python3
"""Generate a labeled synthetic credit-detection corpus with FFmpeg.

Each clip has a known ground-truth credit-start time. The corpus deliberately
spans archetypes that stress different detection strategies:

  - black_scroll   : white text scrolling on black (classic; blackframe should win)
  - color_card     : white text on a solid non-black background (blackframe BLIND)
  - over_content   : credit text over a continuing dim video montage (blackframe BLIND)
  - bright_card    : dark text on a bright/white background (luma assumptions inverted)
  - fade_to_black  : content fades gradually to black, then credits (boundary precision)
  - stinger        : credits, mid-credit content stinger, then more credits
  - short_outro    : a near-minimum-duration credit tail (duration gating)
  - dark_noncredit : a long genuinely dark NON-credit scene before real black credits
                     (false-positive trap: the dark scene must NOT be chosen)
  - no_credits     : ends on content; detector must predict "none"

It also emits 3 "seasons" of episodes that share a credit style and a similar
(jittered) credit-start-from-end, including 1-2 deliberately weak episodes per
season that only cross-episode logic can rescue.

Output:
  corpus/clips/<id>.mp4
  corpus/labels.csv   (id, archetype, season, episode, duration_s, has_credits, credit_start_s, notes)

Determinism: lavfi sources + fixed text. Re-running reproduces the same clips
(modulo ffmpeg build differences). Prototypes should consume the dumped signal
CSVs (see dump_signals.py), not the video, so cross-VM ffmpeg drift is irrelevant.
"""
import csv
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
CLIPS = os.path.join(ROOT, "corpus", "clips")
LABELS = os.path.join(ROOT, "corpus", "labels.csv")

W, H, FPS = 320, 180, 10
FONT = "/usr/share/fonts/truetype/lato/Lato-Medium.ttf"

# A busy, high-entropy "content" source. testsrc2 is colourful and detailed.
def content_src(dur, seed=0):
    return f"testsrc2=size={W}x{H}:rate={FPS}:duration={dur}"

# A dim, low-detail but NON-uniform "dark scene" (night shot) — a false-positive
# trap for luma/black thresholds. mandelbrot is detailed; we darken it heavily.
def dark_scene_src(dur):
    return (f"mandelbrot=size={W}x{H}:rate={FPS}",)

CREDIT_LINES = [
    "Directed by A. Director", "Executive Producer J. Smith", "Produced by K. Jones",
    "Written by L. Writer", "Director of Photography", "Edited by M. Editor",
    "Music by N. Composer", "Cast in order of appearance", "Costume Design",
    "Production Designer", "Casting by", "Visual Effects by",
]

def drawtext(text, y, color="white", size=14, x=20, enable=None):
    t = text.replace(":", r"\:").replace("'", "")
    e = f":enable='{enable}'" if enable else ""
    return f"drawtext=fontfile={FONT}:text='{t}':fontcolor={color}:fontsize={size}:x={x}:y={y}{e}"

def scrolling_credits(bg, dur, color="white", line_h=22, speed=18):
    """A vertical credit roll over background `bg` for `dur` seconds."""
    layers = []
    for i, line in enumerate(CREDIT_LINES):
        # y starts below frame and moves up; staggered per line
        y = f"h-(t*{speed})+{i*line_h}"
        layers.append(drawtext(line, y=y, color=color, size=13))
    return f"{bg}," + ",".join(layers)

def run(cmd):
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        sys.stderr.write(" ".join(cmd[:12]) + " ...\n" + r.stderr[-1500:] + "\n")
        raise SystemExit(f"ffmpeg failed (rc={r.returncode})")

def encode(out, filter_complex, dur, gop=20):
    cmd = [
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-filter_complex", filter_complex, "-map", "[out]",
        "-t", str(dur), "-r", str(FPS),
        "-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "veryfast",
        "-g", str(gop), "-keyint_min", str(gop), "-x264-params", "scenecut=40",
        out,
    ]
    run(cmd)

def black_bg(dur):
    return f"color=c=black:size={W}x{H}:rate={FPS}:duration={dur}"

def color_bg(dur, c="0x1a2a6c"):
    return f"color=c={c}:size={W}x{H}:rate={FPS}:duration={dur}"

def white_bg(dur):
    return f"color=c=white:size={W}x{H}:rate={FPS}:duration={dur}"

rows = []

def add(idv, archetype, season, episode, dur, has_credits, credit_start, fc, notes="", gop=20):
    out = os.path.join(CLIPS, f"{idv}.mp4")
    encode(out, fc, dur, gop=gop)
    rows.append(dict(id=idv, archetype=archetype, season=season, episode=episode,
                     duration_s=dur, has_credits=int(has_credits),
                     credit_start_s=("" if credit_start is None else round(credit_start, 3)),
                     notes=notes))
    print(f"  built {idv:28s} archetype={archetype:14s} start={credit_start}")

def content_then(bg_credit_filter, content_dur, credit_dur):
    """[content][credit] concat. credit filter already includes its bg + text."""
    return (
        f"{content_src(content_dur)}[c];"
        f"{bg_credit_filter}[cr];"
        f"[c][cr]concat=n=2:v=1[out]"
    )

def build_singles():
    cs = 30.0
    # 1. classic black scroll
    add("black_scroll", "black_scroll", "", "", 55, True, cs,
        content_then(scrolling_credits(black_bg(25), 25), 30, 25),
        "white-on-black roll")
    # 2. color card (non-black) -> blackframe blind
    add("color_card", "color_card", "", "", 55, True, cs,
        content_then(scrolling_credits(color_bg(25), 25), 30, 25),
        "white text on navy")
    # 3. credits over continuing (dim) content montage
    over = (f"testsrc2=size={W}x{H}:rate={FPS}:duration=25,eq=brightness=-0.35:saturation=0.4,"
            + ",".join(drawtext(l, y=f"h-(t*18)+{i*22}", color="white", size=13) for i, l in enumerate(CREDIT_LINES)))
    add("over_content", "over_content", "", "", 55, True, cs,
        content_then(over, 30, 25), "text over dim montage")
    # 4. bright card: dark text on white
    add("bright_card", "bright_card", "", "", 55, True, cs,
        content_then(scrolling_credits(white_bg(25), 25, color="black"), 30, 25),
        "dark text on white")
    # 5. fade to black then credits
    fade = (f"{content_src(30)},fade=t=out:st=27:d=3[c];"
            f"{scrolling_credits(black_bg(25), 25)}[cr];[c][cr]concat=n=2:v=1[out]")
    add("fade_to_black", "fade_to_black", "", "", 55, True, 30.0, fade, "3s fade then roll")
    # 6. stinger: credits, content stinger, more credits  (credit start = first block)
    stinger = (
        f"{content_src(20)}[c];"
        f"{scrolling_credits(black_bg(12), 12)}[cr1];"
        f"{content_src(6)}[s];"
        f"{scrolling_credits(black_bg(18), 18)}[cr2];"
        f"[c][cr1][s][cr2]concat=n=4:v=1[out]"
    )
    add("stinger", "stinger", "", "", 56, True, 20.0, stinger, "credits-stinger-credits")
    # 7. short outro near minimum duration (18s tail, just above the 15s min)
    add("short_outro", "short_outro", "", "", 48, True, 30.0,
        content_then(scrolling_credits(black_bg(18), 18), 30, 18), "~18s tail")
    # 8. dark non-credit scene mid-episode, real black credits at end (FP trap)
    trap = (
        f"{content_src(15)}[c1];"
        f"mandelbrot=size={W}x{H}:rate={FPS},eq=brightness=-0.45,trim=duration=15,setpts=PTS-STARTPTS[dk];"
        f"{content_src(10)}[c2];"
        f"{scrolling_credits(black_bg(18), 18)}[cr];"
        f"[c1][dk][c2][cr]concat=n=4:v=1[out]"
    )
    add("dark_noncredit_trap", "dark_noncredit", "", "", 58, True, 40.0, trap,
        "dark scene 15-30s is NOT credits; real credits at 40s")
    # 9. no credits at all
    add("no_credits", "no_credits", "", "", 45, False, None,
        f"{content_src(45)}[out]", "ends on content")
    # 10. sparse keyframes variant of color card (stress boundary precision)
    add("color_card_sparse", "color_card", "", "", 55, True, cs,
        content_then(scrolling_credits(color_bg(25), 25), 30, 25),
        "navy credits, sparse GOP", gop=50)

def build_seasons():
    # Each season shares a style + base credit-start-from-end with jitter; includes
    # weak episodes (very short or ambiguous) that cross-episode logic should rescue.
    seasons = [
        # (season, style_bg_fn, color, base_from_end, jitter list per ep, weak flags)
        ("S1_black", lambda d: black_bg(d), "white", 22),
        ("S2_color", lambda d: color_bg(d, "0x223344"), "white", 20),
        ("S3_over",  None, "white", 24),
    ]
    jitters = [0.0, 1.5, -1.0, 0.5, -2.0]
    weak_eps = {3, 4}  # episodes 4 and 5 (0-indexed 3,4) are weak
    for (season, bgfn, color, base_fe) in seasons:
        ep_total = 50
        for ep in range(5):
            fe = base_fe + jitters[ep]
            cstart = ep_total - fe
            weak = ep in weak_eps
            cdur = fe
            idv = f"{season}_E{ep+1:02d}"
            if season == "S3_over":
                # credits over dim montage
                credit = (f"testsrc2=size={W}x{H}:rate={FPS}:duration={cdur},eq=brightness=-0.3:saturation=0.4,"
                          + ",".join(drawtext(l, y=f"h-(t*16)+{i*22}", color=color, size=13)
                                     for i, l in enumerate(CREDIT_LINES)))
            else:
                # weak episodes: reduce contrast / fewer text lines to make signal faint
                if weak:
                    credit = f"{bgfn(cdur)}," + ",".join(
                        drawtext(l, y=f"h-(t*16)+{i*26}", color="0x999999", size=11)
                        for i, l in enumerate(CREDIT_LINES[:4]))
                else:
                    credit = scrolling_credits(bgfn(cdur), cdur, color=color)
            fc = content_then(credit, ep_total - cdur, cdur)
            add(idv, f"season_{season}", season, ep + 1, ep_total, True, cstart, fc,
                ("weak signal" if weak else "normal"))

def main():
    os.makedirs(CLIPS, exist_ok=True)
    print("Generating singles...")
    build_singles()
    print("Generating seasons...")
    build_seasons()
    with open(LABELS, "w", newline="") as f:
        wr = csv.DictWriter(f, fieldnames=["id", "archetype", "season", "episode",
                                           "duration_s", "has_credits", "credit_start_s", "notes"])
        wr.writeheader()
        wr.writerows(rows)
    print(f"\nWrote {len(rows)} clips + labels -> {LABELS}")

if __name__ == "__main__":
    main()
