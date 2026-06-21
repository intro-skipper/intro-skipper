# Theory 3 — Refine / Simplify the Credit Detector: Findings

**Scope.** Audit + ablation + a concrete C# improvement for `CreditsBlackFrameAnalyzer`
and its `Analyzers/Credits/*` subsystem, measured on the committed synthetic corpus
(24 credited clips + 1 no-credits) via the gold-baseline runner.

**Headline result (real analyzer over the corpus, via `runner`):**

| metric | gold baseline | theory3_refine | Δ |
|---|---|---|---|
| hit @ ±2s | 41.7% (10/24) | **75.0% (18/24)** | +8 clips |
| miss-rate | 58.3% | **25.0%** | −8 clips |
| false-pos | 0% (0/1) | **0% (0/1)** | held |
| MAE (black credits only) | 0.01s | **0.01s** | held (path untouched) |
| MAE (all detected) | 0.01s | 0.17s | +0.16s (new keyframe-granular non-black hits) |
| **OVERALL** | **17.4** | **56.2** | **×3.2** |

The win is entirely the 8 non-black *card* credits (`color_card`, `color_card_sparse`,
`bright_card`, `S2_color_E01..E05`) rescued by a cheap entropy/saturation gate, with the
frame-accurate black path and the 0% false-positive rate left bit-for-bit intact.

---

## A. Audit

### Top 3 findings

**A1 — Architectural blindness to non-black credits is the whole 58% miss (not a bug, but the opportunity).**
`DetectCreditsAsync` reasons *only* about `pblack` per keyframe
(`CreditsBlackFrameAnalyzer.cs` → `DetectBlackFramesAsync` → `CreditSceneBuilder`). Any
credit not rendered as text-on-black produces zero black keyframes, so `DetectCreditScenes`
returns empty, `DetectCreditSceneCandidates` returns empty, and the method returns `null`.
Every one of the 14 baseline misses is a non-black archetype (`color_card`, `over_content`,
`bright_card`, `S2_color`, `S3_over`). The signal dump shows the fix is free: a near-uniform
"card" background has **luma entropy ≤ 0.26** versus **≥ 0.52** for busy content and
**0.58–0.69** for dark non-credit scenes — a clean, large-margin separator emitted by stock
FFmpeg in the same keyframe decode. This is what the entropy fallback exploits (Part C).

**A2 — Adaptive density and blackdetect interval-recovery are inert on realistic inputs (≈150 LOC + an extra FFmpeg pass that the ablation shows contributes 0.0).**
Two of the three "advanced" layers move *nothing* on the corpus (Part B): every metric is
identical with them on or off. They exist as:
- *Adaptive density* — `CreditDetectionPolicy.ComputeMinimumBlackFrameDensity` (`CreditDetectionPolicy.cs:44`), median-scaling the density floor, only relaxes when ≥3 low-density scenes coexist (`:52`, `:62`).
- *Interval recovery* — `DetectBlackIntervalsForCandidatesOrEmptyAsync` + `BuildIntervalProbeRanges` + `CreditSceneBuilder.DetectIntervalSupportedCreditScenes` + `FindSupportingInterval` (`CreditSceneBuilder.cs:342`), a second `blackdetect` decode to rescue sparse-GOP black credits.

Neither is reachable by the realistic black-credit clips (the weak season episodes still have
~98% black density). They are defended **only** by their own synthetic unit tests
(`TestAdaptiveDensity_*`, `TestDetectCreditsAsync_*Interval*`). Verdict: keep *for now* (they
may matter on real sparse-GOP / very-dark shows the synthetic corpus can't represent), but they
are unjustified by any measured input here and are the first place to look when simplifying.

**A3 — `RankCreditCandidates` prioritizes interval-support above recency, but that dimension is dead in production and alive only in tests.**
`CreditsBlackFrameAnalyzer.cs:355-356` orders `OrderByDescending(HasIntervalSupport).ThenByDescending(Index)`.
In the live pipeline `blackIntervals` is non-empty **only** in the two recovery branches, where
*every* candidate scene is interval-derived — so `HasIntervalSupport` is uniform across
candidates and the ordering collapses to "latest scene wins". The mixed case (some supported,
some not) where the first key changes the pick is constructed *only* by the
`TestCreditCandidateScoring_CorpusMatrix` theory calling `RankCreditCandidates` directly. So the
rule is pinned by tests it can't reach in production, and where it *could* matter it is a
latent correctness risk: a long mid-episode fade-to-black with interval support would outrank
later, sparser real end credits. Recommendation: either feed mixed candidates through the real
pipeline (and accept the risk deliberately) or drop the interval-priority key and rank purely by
recency, which is all production actually uses.

### Secondary correctness / edge-case risks

- **`NormalizeThreshold` 1st-percentile degenerates for < 100 keyframes** —
  `CreditsBlackFrameAnalyzer.cs:280` computes `percentileIndex = (int)(frames.Count * 0.01)`,
  which is **0** for any scan under 100 keyframes (all corpus clips: 11–29 keyframes). The
  "floor" then becomes the single least-black keyframe (`:281`), so one bright/among-content
  outlier keyframe can shift `minimum`/`sceneChange` for the entire episode. Harmless on dense
  scans, but exactly the short-window / sparse-GOP credits case the interval layer targets is
  where it is least robust. Consider `max(0, count/100 - 1)` with an explicit small-sample
  fallback (and note the existing tests pin the 100-frame behavior, so any change must preserve it).
- **`SelectProbeMinimum` uses `.First()` with no guard** — `CreditsBoundaryHelper.cs:61`
  throws `InvalidOperationException` if a scene's `StartFrame` is absent from `frames`. Today every
  `StartFrame` traces back to a real keyframe, so it is latent rather than live, but it is one
  refactor away from a crash inside the refinement path; `FirstOrDefault` + guard is cheap insurance.
- **`MergeNearbyScenes` resets `mergeSearchStart = 0` every iteration** —
  `CreditSceneBuilder.cs:169` re-initializes inside the loop, defeating the `ref searchStart`
  carry-forward in `MeetsBlackFrameDensity` and making each merge check rescan from frame 0
  (O(n²)). Correct, just wasteful; hoist the variable out of the loop.
- **Inconsistent median conventions** — `EstimateMaximumInRunGap`
  (`CreditSceneBuilder.cs:339`) uses `gaps[gaps.Count / 2]` (upper-middle element) while
  `ComputeMinimumBlackFrameDensity` computes a true even-count average median. Minor, but two
  "median" helpers that disagree invite drift.

None of these were changed: the black path is load-bearing and frame-accurate, and the hard
requirement is to not regress it. They are documented for a follow-up hardening pass.

---

## B. Ablation

**Method.** Each layer was toggled at runtime via temporary env gates injected into the real
analyzer (`theory3_refine/run_ablation.sh`), built once, then run through the **gold runner** over
the corpus and scored with `score.py`. This avoids dead-code warnings (the repo treats warnings as
errors) and needs no per-variant rebuild. Raw per-variant JSON/CSV in `theory3_refine/ablation/`.

| variant | hit@2 | miss | FP | MAE | OVERALL | what moved |
|---|---|---|---|---|---|---|
| full (all layers) | 41.7% | 58.3% | 0% | **0.01s** | 17.4 | — |
| − boundary refinement | 41.7% | 58.3% | 0% | **0.31s** | 17.4 | MAE only |
| − adaptive density | 41.7% | 58.3% | 0% | 0.01s | 17.4 | **nothing** |
| − interval recovery | 41.7% | 58.3% | 0% | 0.01s | 17.4 | **nothing** |
| core only (all three off) | 41.7% | 58.3% | 0% | 0.31s | 17.4 | = − boundary |

**Per-clip mover (boundary refinement, the only one):** it sharpens 3 of the 10 black hits whose
true start falls *between* keyframes — `S1_black_E02` 28.0→26.5, `S1_black_E03` 30.0→29.0,
`S1_black_E04` 28.0→27.5 (all already hits within ±2s; refinement just removes the 0.5–1.5s
keyframe-snap error). No clip changes hit/miss status under any toggle.

**Verdict — which layers earn their keep:**
- **Boundary refinement → keep.** Sole source of the headline 0.01s MAE; one extra bounded
  FFmpeg probe per detection. Cheap, and the only layer with a measurable effect.
- **Adaptive density → does not earn its keep here.** Zero corpus effect; justified only by unit
  tests. Keep pending real sparse/low-contrast validation; candidate to simplify.
- **Interval recovery → does not earn its keep here.** Zero corpus effect for the largest single
  block of code + an extra `blackdetect` decode; justified only by unit tests. Same recommendation.

The corpus simply contains no input that needs adaptive-density or interval-recovery — so on this
evidence they are pure complexity. They are retained (removing them would delete behavior pinned by
existing tests, which must stay green) but flagged as the prime simplification targets once real
sparse-GOP footage is available to prove or disprove their value.

---

## C. Improvement — entropy/saturation non-black credit fallback

### Signal characterization (median per region, from `theory3_refine/analyze_signals.py`)

| region | entY | yavg | satavg | black-frame sees it? |
|---|---|---|---|---|
| content (testsrc2, all clips) | 0.53 | 122 | 108 | no |
| **card credits** (color/white/navy) | **0.07–0.20** | 54–235 | 0–33 | **no** (the gap) |
| black credits | 0.06–0.21 | 17–25 | 0 | yes |
| dark NON-credit scene (mandelbrot) | 0.58–0.69 | 20–33 | 42–54 | no (FP trap) |
| over-content credits (dim montage) | 0.52–0.58 | 48–57 | 37–42 | no |

The decisive structure: a **near-uniform card background is low-entropy regardless of its colour
or brightness** (text occupies few pixels, so the luma histogram stays peaked). Busy content and
detailed dark scenes are high-entropy. `entY < 0.35` separates every card credit (≤0.26) from all
content and all dark scenes (≥0.52) with a wide margin, and is luma-agnostic so it works for white
text on navy, dark text on white, and grey-on-slate alike.

### Design (shipped)

When the black-frame path returns no valid credits **and** `DetectNonBlackCredits` is enabled, run
one extra keyframe decode emitting luma entropy + mean saturation, then accept the **latest**
sustained run of *credit-card* keyframes (≥ `MinimumCreditsDuration`). A keyframe is a credit card iff:

```
entropy  < EntropyCreditMaximum (0.35)      // near-uniform background → card or black
saturation < SaturationCreditMaximum (96)   // muted; generous ceiling vs content's ~108
```

Both signals come from stock FFmpeg (`entropy,signalstats,metadata=print`) in the *same* keyframe
decode shape as the black-frame scan. The entropy ceiling is simultaneously the **dark-scene
false-positive suppressor** required by goal (i): a detailed dark scene is high-entropy and can
never match, and the fallback only runs after the black path has already declined — so on
`dark_noncredit_trap` the real black credits are found first and the fallback never even runs.

**Why it's safe by construction:**
- The black path is byte-for-byte unchanged (extracted verbatim into `DetectBlackFrameCreditsAsync`); all 10 black hits and their 0.01s MAE are preserved — verified identical to the gold baseline.
- The fallback can only *add* detections, never alter black ones, so the 0% FP and black MAE cannot regress.
- Run unconditionally as a stress test (no black-path shield), the entropy gate still picks the trap's real credits at 40s (not the dark scene at 16s) and never fires on `no_credits` — it is robust on its own merits, not merely shielded.

### Code (idiomatic, behind existing config style)

- `Data/KeyframeVisual.cs` — new per-keyframe record (time, entropy, saturation).
- `Data/CacheEntryType.cs` + `Helper/ConfigHasher.cs` — new `KeyframeVisual` cache type (parity with the other scans).
- `FFmpeg/FFmpegOutputParser.cs` — `ParseKeyframeVisuals` (parses `metadata=print` blocks; luma plane only).
- `FFmpeg/IFFmpegService.cs` + `FFmpegService.cs` — `DetectKeyframeVisualsAsync` (one-pass `entropy,signalstats,metadata=print`, cached; `metadata=print` added to the info-loglevel gate).
- `Analyzers/Credits/CreditDetectionPolicy.cs` — `EntropyCreditMaximum`, `SaturationCreditMaximum`.
- `Analyzers/Credits/CreditEntropyFallback.cs` — pure detection logic (`FindCreditRange`, `IsCreditCardKeyframe`); reuses the black path's gap/“latest-wins” conventions.
- `Analyzers/CreditsBlackFrameAnalyzer.cs` — `DetectCreditsAsync` now: black path → fallback (if enabled).
- `Configuration/PluginConfiguration.cs` — `DetectNonBlackCredits` (default `true`).
- `IntroSkipper.Tests/TestBlackFrames.cs` — +13 tests (parser, predicate, fallback runs/duration/latest-wins/dark-scene rejection, analyzer integration, config gate, black-path-wins). **77/77 in the `TestBlackFrames` filter; full build clean (0 warnings).**

### Before / after (`score.py`, real analyzer via `runner`)

```
gold baseline    : hit@2 41.7%  miss 58.3%  fp 0.0%  MAE 0.01s  OVERALL 17.4
theory3_refine   : hit@2 75.0%  miss 25.0%  fp 0.0%  MAE 0.17s  OVERALL 56.2
```

Per-archetype: `color_card` 0/2→2/2, `bright_card` 0/1→1/1, `season_S2_color` 0/5→5/5; all black
archetypes unchanged; `no_credits` still none; `over_content`/`S3_over` still none (see below).

### Over-content rescue: measured, but rejected (over-fit / FP-risky)

A second branch — `entY ≥ 0.35 AND yavg < 75 AND satavg < 75` — rescues the remaining 6
dim-montage clips and reaches **OVERALL 100.0** on the corpus (`theory3_refine/predictions/theory3_proto_card_over.csv`).
It is deliberately **not shipped**:
- A stress test running the fallback unguarded merges the `dark_noncredit_trap` dark scene
  (16–30s) straight into the real credits (run 16→56s) → it would emit a false 16s start. It only
  avoids that here because the black path finds the trap's real credits first and shields it.
- Its thresholds are *absolute* luma/saturation values tuned to one synthetic source (testsrc2 at
  122/108). Real content luma/saturation varies far more, and the only on-corpus negative is one
  `no_credits` clip — 100% with a single negative is the textbook over-fit signature.

Recommendation: keep it out of the default path; revisit only with a corpus of real dark-but-
non-credit tails to prove FP discipline. The entropy *card* gate, by contrast, generalizes (any
near-uniform background) and is robust even unshielded.

---

## Recommendation — keep / cut / add

- **ADD (shipped):** the entropy/saturation card fallback. +8 clips, 17.4 → 56.2, 0 FP, black MAE
  held, ~150 LOC fully isolated behind `DetectNonBlackCredits`. The single highest-value change.
- **KEEP:** boundary refinement (only layer that earns its keep — owns the 0.01s MAE).
- **CUT-CANDIDATES (don't cut yet):** adaptive density + interval recovery — provably inert on every
  realistic input here; retained only because existing unit tests pin them and may reflect real
  sparse-GOP cases. Validate on real footage; if they stay inert, deleting them removes ~150 LOC
  and one FFmpeg pass.
- **HARDEN (follow-up):** simplify `RankCreditCandidates` to pure recency (its interval key is dead
  in production); fix the `NormalizeThreshold` small-sample percentile; guard `SelectProbeMinimum`'s
  `.First()`; hoist `mergeSearchStart`.
- **DO NOT ADD:** the over-content luma/saturation branch — over-fit and FP-risky without real negatives.

### Reproduce

```bash
cd tools/credit-research
python3 generate_corpus.py                                   # clips (needs ffmpeg + Lato font)
dotnet run --project runner -c Release -- corpus/labels.csv corpus/clips predictions/baseline_current_csharp.csv
dotnet run --project runner -c Release -- corpus/labels.csv corpus/clips predictions/theory3_refine.csv
python3 score.py predictions/theory3_refine.csv --tol 2,5
python3 theory3_refine/analyze_signals.py                    # signal separability
bash   theory3_refine/run_ablation.sh                        # layer ablation
```
