# Credit-detection research harness

A reproducible, **measurement-driven** sandbox for exploring how to build the
`CreditsBlackFrameAnalyzer` (Jellyfin Intro-Skipper, branch `Detect-Credit-Scenes`)
in the most effective way. Every approach is scored on the same labeled corpus so
theories are compared with numbers, not vibes.

## Why this exists

The production analyzer scans keyframes once with FFmpeg `blackframe` and reasons
purely about the **black percentage** of each keyframe. That is frame-accurate for
credits rendered as text on black, and **completely blind** to credits on coloured
cards, bright backgrounds, or over continuing video. Measured on this corpus the
current analyzer scores:

| metric        | current analyzer |
|---------------|------------------|
| hit @ ±2s     | **41.7%** (10/24) |
| miss-rate     | 58.3% (14/24, all non-black) |
| false-pos     | 0% (0/1) |
| MAE (detected)| **0.01s** (frame-accurate when it fires) |
| OVERALL       | **17.4** |

The opportunity is the 58% it cannot see, **without** sacrificing the 0% false-positive
rate or the frame-accuracy it already has on black credits.

## Hard constraint: FFmpeg-emittable signals only

The plugin is C# and shells out to stock FFmpeg. No OpenCV / Tesseract / ML models.
So prototypes may only use signals FFmpeg can emit and we can parse. The harness
pre-computes those per keyframe so prototypes never touch video and port straight
to C#:

`corpus/signals/<id>.csv`, one row per keyframe (`-skip_frame nokey`, threshold=32):

| column   | source                                   | meaning |
|----------|------------------------------------------|---------|
| `t`      | pts_time                                 | keyframe time (s) |
| `pblack` | `blackframe` `pblack`                    | % of frame below luma threshold (what the analyzer uses today) |
| `entY/U/V`| `entropy` `normalized_entropy`          | 0..1 histogram entropy per plane (low = uniform/plain background) |
| `yavg`   | `signalstats` `YAVG`                     | mean luma 0..255 |
| `satavg` | `signalstats` `SATAVG`                   | mean saturation (low = greyscale/credits cards) |
| `hueavg` | `signalstats` `HUEAVG`                   | mean hue |
| `scd`    | `scdet` `score`                          | scene-change score 0..100 (spikes at cuts) |
| `edge`   | `edgedetect` -> `signalstats` `YAVG`     | edge/text density proxy 0..255 |

All of the above are produced in **one keyframe decode** (split filter graph), so a
multi-signal detector costs ~the same as today's single `blackframe` scan.

## Ground truth

`corpus/labels.csv`: `id, archetype, season, episode, duration_s, has_credits, credit_start_s, notes`.

Archetypes (24 credited clips + 1 no-credits):
- `black_scroll` — white text on black (baseline's home turf)
- `color_card`, `color_card_sparse` — white text on navy (blackframe blind; sparse = wide GOP)
- `over_content` — credit text over a dim continuing montage (blackframe blind)
- `bright_card` — dark text on white (luma assumptions inverted; adversarial)
- `fade_to_black` — 3s fade then roll (boundary precision)
- `stinger` — credits / content stinger / credits (must pick the FIRST credit block @20s)
- `short_outro` — ~18s tail just above the 15s minimum (duration gating)
- `dark_noncredit` — a long genuinely dark NON-credit scene mid-episode, real black credits at the end (false-positive trap: must NOT pick the dark scene @15-30s)
- `no_credits` — ends on content (must predict none)
- `season_S1_black / S2_color / S3_over` — 3 seasons × 5 episodes, shared style + similar
  credit-start-from-end with jitter; episodes 4–5 are deliberately **weak** (faint, low-contrast)
  to reward cross-episode reasoning.

## Workflow

```bash
# 1. (re)generate clips + labels      (needs ffmpeg; writes corpus/clips/*.mp4)
python3 generate_corpus.py
# 2. dump per-keyframe signal CSVs     (committed; prototypes read these)
python3 dump_signals.py
# 3. run a predictor -> predictions/<name>.csv  (id,predicted_start ; empty = none)
python3 baselines/predict_blackframe.py        # reference template
# 4. score it
python3 score.py predictions/<name>.csv --tol 2,5
```

The C# gold baseline (real analyzer over the clips) is reproduced by:
```bash
dotnet run --project runner -c Release -- corpus/labels.csv corpus/clips predictions/baseline_current_csharp.csv
```
A pure-CSV reimplementation (`baselines/predict_blackframe.py`) reproduces the gold
baseline's hit-rate exactly, which validates that CSV-only prototypes reflect reality.

## Rules of engagement for theory prototypes

1. Input = `corpus/signals/*.csv` only. Output = `predictions/<theory>.csv` (`id,predicted_start`).
2. Use only columns derivable from stock FFmpeg (the schema above). If you want a new
   signal, it must be expressible as an FFmpeg filter and added to `dump_signals.py`.
3. Report `score.py` output. **Beat OVERALL 17.4**; the bar is raising non-black hit-rate
   while keeping false-pos at 0 and not regressing black-credit MAE.
4. Keep the decision logic portable to C# (no heavy deps) — it will be reimplemented in
   the analyzer if it wins.
5. Note `stinger` wants the FIRST block (@20s) and `dark_noncredit` must avoid the dark
   scene — both test false-positive discipline, not just recall.

> The corpus is **synthetic** (FFmpeg-rendered). It deliberately isolates the signal
> structure of each archetype; it is not a substitute for real-world footage. Treat
> rankings as directional evidence about which signals generalize, validated against
> the 3 real `IntroSkipper.Tests/fingerprints/blackframe-alt-*` dumps where applicable.
