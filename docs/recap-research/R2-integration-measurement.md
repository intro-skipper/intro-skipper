# [Recap RFC R2] Integration + the first evidence-based comparison of recap detectors

**Status:** Measurement spike. Builds on RFC D (harness, PR #808) and integrates spikes A (subtitles,
PR #805) and C (hardening, PR #806). The shippable artifact is **numbers**: a reproducible,
harm-aware comparison of four recap-detector configurations on a synthetic-representative dataset.

> **TL;DR (measured, not asserted).** On a 36-episode synthetic-representative dataset (23 with a
> recap, 13 without), the full **chapter → subtitle → hardened-sting ensemble (+A+C)** is the best
> configuration on every axis that matters: **recall 0.913** (vs the shipped baseline's 0.435),
> **false-positive rate 0.077** (vs 0.308), and — the metric the inherited harness was blind to —
> **0 seconds of content-skip and 0 fired-but-wrong** detections (vs the baseline's **325 s of
> wrongly-skipped cold-open story across 6 fired-but-wrong cold-open recaps**). The two signals are
> **complementary, not redundant**: hardening (C) is what removes the *harm* (content-skip,
> fired-but-wrong, false positives); subtitles (A) is what adds *reach* (after-intro recaps,
> recaps with no shared audio sting). Neither alone is sufficient. **This is a directional result
> about the logic, not a real-world accuracy claim** — see §8.

---

## 1. What was assembled

This branch merges three round-1 spikes onto one trunk and reconciles their overlap:

| Spike | Branch / PR | What it contributes |
| --- | --- | --- |
| **D** (this trunk) | `recap-rfc-d-ensemble-eval` / #808 | The media-free metric harness (`IntroSkipper/Evaluation/*`): dataset schema, IoU/MAE/recall/FP, runner, report. |
| **A** (subtitles) | `recap-rfc-a-subtitles` / #805 | `IntroSkipper/Subtitles/*`: subtitle parser, anchored multilingual "Previously on" matcher, dense-cue clustering, black-frame end snap. Finds **non-zero-start** and **after-intro** recaps with **no cross-episode** comparison. |
| **C** (hardening) | `recap-rfc-c-harden` / #806 | Rewrites `RecapDetectionHelper` for cold-open-aware start, earliest-valid montage end, and a false-positive guard against the opening theme. |

Both merges were clean (A is purely additive; C edits the analyzers; D adds the harness — no real
conflicts). After merging A+C the suite was 429 pass / 1 fail (430 total); this spike adds 21 tests,
giving **450 pass / 1 fail** (451 total). The single failure is the pre-existing
`TestAudioFingerprinting.TestSilenceDetection` ffmpeg float-precision flake (unrelated; documented in
RFC D §5). The strict analyzer build (`TreatWarningsAsErrors` + `AllEnabledByDefault`) stays
warning-clean.

---

## 2. Reconciliation: one boundary step, not two (RFC D §2.3)

Spikes A and C **both** computed recap start/end, with different and partly conflicting policies:

| | Spike A (`SubtitleRecapSegmentBuilder`) | Spike C (`RecapDetectionHelper`) |
| --- | --- | --- |
| **Start** | opt-in snap-to-0 when the anchor cue begins within 2 s of 0 (off by default → keeps the cue start) | cold-open-aware: `≤5 s → 0`, else anchor to the fade just before the candidate, else keep |
| **End** | snap the cue-cluster end forward to a black frame (≤6 s, 1 s backward tolerance) | discover the *earliest valid* montage-end black frame after the sting, duration-bounded |
| **Never blanket-0?** | honored (opt-in) | honored (cold-open anchor) |

**The conflict.** Two start policies and two end policies in two files is exactly the
"boundary semantics differ by signal" defect RFC D flagged (finding 4). Left as-is, the subtitle
tier and the sting tier would disagree on where a recap begins and ends for the *same* episode.

**The resolution (implemented).** `RecapDetectionHelper` — which spike C already established as "the
single source of truth for the recap scan window and boundary logic" — gains a tier-agnostic
`ReconcileBoundaries(candidateStart, candidateEnd, blackFrameTimes, options)` that any tier feeds a
candidate interval into:

1. **Start** is resolved by one shared `ResolveStartCore` (extracted from C's `ResolveRecapStart`, which now delegates to it): snap to 0 only when the candidate already opens the episode, otherwise anchor to the fade in the cold-open lead-in window, **never blanket-snap to 0** (RFC D §2.3 #3).
2. **End** is refined by one shared `RefineEndToBlackFrame`: snap to the nearest fade within a small window — a short backward tolerance plus a forward snap window (generalizes A's end snap *and* C's end-refinement so end semantics no longer differ by signal, RFC D §2.3 #2).
3. The result is duration- and ceiling-clamped, or rejected.

In the ensemble, **spike A becomes a pure localizer** (its native start/end snapping disabled): the
phrase matcher + cue clustering find *where* the recap is, and the shared step owns the *boundaries*.
The sting tier keeps its montage-end *discovery* (`SelectMontageEnd`) — because its candidate (a 3 s
"previously on" jingle) is only an anchor, not the recap span — but its **start** flows through the
same `ResolveStartCore`. So there is now exactly one start policy and one end policy across all tiers.
Chapters are explicit author metadata and bypass boundary inference (the shipped chapter path does
the same); they are validated against the duration bounds only.

This refactor is behavior-preserving for spike C: all 26 of its hardening tests still pass.

---

## 3. The tiered ensemble (RFC D §2.1)

`RecapTierPipeline.Detect(inputs, config, matcher)` runs the precedence tiers in order, each behind
an enable flag, each skipped when an earlier tier already resolved the episode (the existing chain's
"first valid wins + later analyzers skip `Analyzed` episodes," named and ordered):

```
Tier 1  Chapter   explicit marker [start,end]            (authoritative; bypasses inference)
Tier 2  Subtitle  anchored "Previously on" + cue cluster (spike A localizer + shared reconcile)
Tier 3  Sting     shared audio sting + black frames      (spike C hardened, or legacy)
```

It is a **thin orchestration for measurement** (RFC D's harness stays inert in production), but it
executes the **real** spike A and C code over each episode's available inputs and returns the winning
`Segment`-equivalent interval. Full production wiring into `BaseItemAnalyzerTask` is the obvious
follow-up; it was kept secondary so the measurement could be produced now.

Key faithful detail: the **subtitle tier is not intro-clamped** (it uses A's 150 s anchor window),
which is precisely how it reaches an after-intro recap; the **sting tier is intro-clamped** (C's
`ComputeMaximumBoundary`), which is precisely why it cannot.

---

## 4. Harm-aware metrics (why recall/IoU alone mislead)

The inherited harness buckets **"detector stayed silent"** and **"detector fired on the wrong
interval"** *both* as false negatives, and IoU/MAE are symmetric. That hides the single most
important real-world difference: a baseline that forces the start to 0 will **swallow the cold
open / real story**, which is far worse for a viewer than a silent miss — yet stock recall and IoU
rate them identically. This spike therefore extends the metrics (additively; the original
confusion-matrix and rate definitions are unchanged, so RFC D's 29 tests still pass):

- **Content-skip seconds** — on a has-recap episode that fired, the seconds of the detection that
  fall **outside** the true recap, i.e. non-recap content the viewer wrongly skips
  (`ContentOutsideTruth = dur(detected) − overlap`). This is the **harmful over-reach** direction and
  is weighted heaviest in the verdict.
- **Missed-recap seconds** — the seconds of the true recap **not** covered (`dur(truth) − overlap`).
  The milder under-reach direction (the viewer merely re-watches a few seconds); reported for symmetry.
- **The false-negative bucket is split** into **silent miss** (fired = false; *safe* — no skip
  button shown) and **fired-but-wrong** (fired = true, IoU < τ; *harmful* — a skip over the wrong
  span). They are never collapsed into one number.
- **False-positive rate** on no-recap episodes stays gated independently of IoU.

IoU / start-MAE / end-MAE are retained as diagnostics. An FP on a no-recap episode also skips its
entire detected span; that harm is captured by the FP rate (and would be the full detection length).

---

## 5. The dataset (`docs/recap-research/R2-scenarios.json`)

36 **synthetic-representative** scenarios, authored in code (`RecapScenarioCatalog`) and serialized to
JSON. Each entry pairs a ground-truth `RecapLabel` with the per-tier `RecapEpisodeInputs` the
detectors see (chapter marker, subtitle cues, sting presence+interval, black-frame times, intro
start, duration). Composition:

- **Shapes:** 9 RecapFirst, 9 ColdOpenThenRecap, 5 AfterIntro, 13 NoRecap (23 with recap / 13 without
  — a deliberately healthy no-recap majority so the FP rate is meaningful).
- **Signal coverage that makes the comparison honest, not rigged:**
  - recaps **with** and **without** a shared sting (the latter unreachable by the audio path);
  - recaps **with** text subtitle cues and **without** (image-sub / no-sub episodes where the
    subtitle tier must abstain and fall back);
  - **chapter-marked** recaps (resolved identically by every config — the high-precision tier that
    already worked);
  - NoRecap **distractors**: recurring multi-second *theme stings* (a baseline false positive the
    hardened guard rejects), a **short studio ident** + fade (slips through *every* config — C's
    documented ceiling), and an incidental **mid-dialogue "previously"** cue (which the anchored
    matcher must, and does, refuse);
  - three "nobody wins" cases (a cold-open recap and an after-intro recap with *no* usable signal,
    plus the short-ident FP) so no configuration scores a misleading 100 %.

Recap durations are realistic (18–55 s) and ≥ the 15 s plugin floor, so the duration bound is not a
confound across configs.

---

## 6. The comparison (produced by the harness actually running)

Generated by `RecapComparisonRunner` over `RecapScenarioCatalog.Default`, scored through
`RecapEvaluator`, regenerable via the test in §9. IoU match threshold = 0.5.

### Aggregate

| metric | baseline (shipped) | +C hardening | +A subtitles | +A+C ensemble |
| --- | --- | --- | --- | --- |
| detection rate (recall) | 0.435 (10/23) | 0.696 (16/23) | 0.870 (20/23) | **0.913 (21/23)** |
| false-positive rate | 0.308 (4/13) | **0.077 (1/13)** | 0.308 (4/13) | **0.077 (1/13)** |
| **fired-but-wrong (harmful)** | **6** | **0** | 1 | **0** |
| silent miss (safe) | 7 | 7 | 2 | 2 |
| **content-skip s — total (harm)** | **325.00** | **0.00** | 55.00 | **0.00** |
| content-skip s — mean/fired | 20.31 (n=16) | 0.00 (n=16) | 2.62 (n=21) | 0.00 (n=21) |
| missed-recap s — total | 0.00 | 0.00 | 15.00 | 0.00 |
| precision | 0.714 | 0.941 | 0.833 | **0.955** |
| F1 score | 0.541 | 0.800 | 0.851 | **0.933** |
| start MAE (s) | 20.31 (n=16) | 0.00 (n=16) | 3.33 (n=21) | 0.00 (n=21) |
| end MAE (s) | 0.00 (n=16) | 0.00 (n=16) | 0.00 (n=21) | 0.00 (n=21) |
| mean IoU | 0.537 | 0.696 | 0.865 | **0.913** |

### Per shape (recall · fired-but-wrong · content-skip total · mean IoU)

| shape (n, withRecap) | baseline | +C | +A | +A+C |
| --- | --- | --- | --- | --- |
| **RecapFirst** (9, 9) | 0.889 · 0 · 0 s · 0.889 | 0.889 · 0 · 0 s · 0.889 | 1.000 · 0 · 0 s · 0.975 | **1.000 · 0 · 0 s · 1.000** |
| **ColdOpenThenRecap** (9, 9) | 0.111 · **6** · **325 s** · 0.372 | 0.778 · 0 · 0 s · 0.778 | 0.778 · 1 · 55 s · 0.802 | **0.889 · 0 · 0 s · 0.889** |
| **AfterIntro** (5, 5) | 0.200 · 0 · 0 s · 0.200 | 0.200 · 0 · 0 s · 0.200 | 0.800 · 0 · 0 s · 0.782 | **0.800 · 0 · 0 s · 0.800** |
| **NoRecap** (13, 0) | FP 0.308 (4/13) | **FP 0.077 (1/13)** | FP 0.308 (4/13) | **FP 0.077 (1/13)** |

---

## 7. Measured verdict

**The +A+C ensemble maximizes recall (0.913) while holding the false-positive rate low (0.077) and
driving the harmful directions to zero (0 content-skip, 0 fired-but-wrong).** It wins or ties on
every metric. The two signals are complementary:

- **Hardening (C) removes the harm.** It is the entire reason content-skip drops from **325 s → 0**,
  fired-but-wrong from **6 → 0**, and FP from **0.308 → 0.077**. The shipped baseline's headline
  number — recall 0.435 — *understates* how bad it is: on cold-open recaps it doesn't just miss, it
  **fires on the wrong span**, forcing the start to 0 and skipping on average **46 s of cold-open
  story per episode** (the `ColdOpenThenRecap` mean start error is 46.4 s). A harm-blind table would
  call that "low recall"; it is actively user-hostile. C fixes it by anchoring the start to the
  cold-open fade.
- **Subtitles (A) add the reach.** It is the entire reason recall climbs past C's ceiling: it lifts
  **AfterIntro from 0.200 → 0.800** (the sting window is clamped at the intro; subtitles are not) and
  recovers recaps with **no shared sting** (e.g. unique per-episode "previously on" narration). But
  **A alone does not touch the sting path**, so its FP rate stays at the baseline's 0.308, and its
  *native* boundaries leave a residual **55 s of content-skip / 3.3 s start-MAE / 15 s missed-recap**
  because its cue cluster starts ~1 s late and isn't cold-open-snapped.
- **Together** the shared reconciliation gives A's localizations C-quality boundaries: the ensemble's
  start MAE and content-skip fall to **0**, and `RecapFirst` mean IoU reaches a perfect 1.000 (the
  reconciler snaps A's ~0.7 s native start error back to 0).

**Boundary-error tradeoff.** End-MAE is 0 for every config here (fades are clean in synthetic data),
so the entire localization story is in the **start**. That is the right emphasis for a skip-button:
a wrong *start* is what skips real content. Real media will have noisier ends; the harness will
surface that when fed real fades.

**Where each tier wins/loses by shape:**
- *RecapFirst* — everyone does well; A/A+C edge ahead only by catching a no-sting case (recall 1.0 vs 0.889).
- *ColdOpenThenRecap* — the baseline's catastrophe (recall 0.111, six fired-but-wrong, 325 s skipped); C *or* A each lifts recall to 0.778, and A+C to 0.889 (the no-subtitle cold-open needs C's hardened sting).
- *AfterIntro* — **only** subtitles/chapters reach it; C cannot (intro-clamped window), so C ties the baseline at 0.200. This is A's structural win.
- *NoRecap* — the FP differentiator: the hardened guard cuts FP from 4/13 to 1/13. The one residual FP (a short studio ident + fade with no intro) slips through **every** config — C's documented ceiling. The mid-dialogue "previously" distractor correctly never fires (A's anchored matching).

**Honest losses kept in the dataset:** one cold-open recap and one after-intro recap with no usable
signal are missed by **all** configs (silent miss), and the short-ident NoRecap is a false positive
for **all** configs. No configuration scores a misleading 100 %.

---

## 8. What this can and cannot prove (read this before quoting a number)

**What the harness executes (real code):** spike A's `RecapPhraseMatcher` (anchored, multilingual)
and `SubtitleRecapSegmentBuilder` (cue clustering); spike C's `RecapDetectionHelper`
(cold-open start resolution, montage-end selection, false-positive guard, the shared
`ReconcileBoundaries`); and RFC D's metric core (`RecapMetrics`/`RecapEvaluator`). These are not
re-implemented for the test — the pipeline calls the merged production methods.

**What it does NOT execute (bypassed):** the upstream **signal extraction** — Chromaprint audio
fingerprinting, ffmpeg black-frame/silence detection, ffprobe/subtitle demux — and therefore **no
real audio or video is decoded**. The shipped **baseline** "legacy sting" path is a *faithful
re-implementation* of `BuildRecapFromChromaprintCandidate → BuildRecapFromBlackFrames` (start forced
to 0, latest black frame, no guard), because the original code was replaced by the C merge; it is not
the original binary.

**Therefore the per-episode inputs are MODELED, not measured.** The sting presence+interval,
black-frame timestamps, subtitle cue text+times, chapter bounds, and intro start are **authored** per
scenario — and authored *cleanly* (crisp stings, unambiguous fades, well-formed cue text). Real
extraction is noisier, lossy, and sometimes wrong. The detections this table scores are what each
detector's logic **would emit given those assumed signals**, not the output of a live run on a media
library.

**So this comparison is DIRECTIONAL, not validation. It can:**
- prove the integrated **logic** behaves as designed and that the tiers compose without breaking each other;
- show the **relative** behavior of the four configurations and the **mechanism** of each difference (why the baseline skips content; why C kills the harm; why A adds reach);
- make the **harm asymmetry** explicit so a story-skipping detector can't hide behind "low recall."

**It cannot:**
- prove real-world accuracy or pick a production default on its own — synthetic inputs are not the real distribution of shows/edit styles, and clean synthetic signals flatter every detector (note the all-zero end-MAE);
- validate the upstream extractors (the part most likely to fail in the field — cf. PR #771 shipping broken);
- choose the IoU threshold or the "good enough" boundary tolerance for the skip-button UX.

**What real validation still needs (unchanged from RFC D §5):** a labeled corpus of **real**
episodes — order 30–50 per shape per major genre, with a healthy no-recap majority, contributed by
multiple users — scored through this same harness via the `RecapDetection.FromInterval` adapter.
Until that exists, every accuracy claim about recap detection — including this one — is a hypothesis.

---

## 9. Reproduce

```
# Unit + integration tests (orchestrator, shared reconciliation, harm metrics, comparison relationships):
dotnet test IntroSkipper.Tests/IntroSkipper.Tests.csproj -p:SkipWebBuild=true \
  --filter "FullyQualifiedName~TestRecapEnsemble"

# Regenerate this table + the scenarios JSON from the harness:
RECAP_R2_OUT_DIR=/tmp/recap-r2 dotnet test IntroSkipper.Tests/IntroSkipper.Tests.csproj \
  -p:SkipWebBuild=true --filter "FullyQualifiedName~Comparison_WritesArtifactsWhenRequested"
#   -> /tmp/recap-r2/R2-comparison.md, R2-scenarios.json, report_*.md
```

The comparison's key relationships (ensemble recall ≥ all; hardened FP < baseline FP; baseline
content-skip > ensemble; baseline fired-but-wrong > ensemble) are **assertions** in
`TestRecapEnsemble`, so a regression that re-introduces story-skipping fails the build, not just a
review.

### File map (added this spike)

| File | Role |
| --- | --- |
| `IntroSkipper/Analyzers/RecapDetectionHelper.cs` | + `ReconcileBoundaries`, `RefineEndToBlackFrame`, shared `ResolveStartCore`, `RecapBoundaryOptions` (the single boundary step). |
| `IntroSkipper/Evaluation/RecapEpisodeInputs.cs` | Per-episode synthetic signal inputs. |
| `IntroSkipper/Evaluation/RecapTier.cs`, `RecapTierOutcome.cs` | Tier enum + pipeline outcome. |
| `IntroSkipper/Evaluation/RecapDetectorConfig.cs` | The four named configurations. |
| `IntroSkipper/Evaluation/RecapTierPipeline.cs` | The tiered orchestration (calls real A/C logic). |
| `IntroSkipper/Evaluation/RecapScenario.cs`, `RecapScenarioSet.cs`, `RecapScenarioCatalog.cs` | Dataset (truth + inputs) + the 36-scenario catalog. |
| `IntroSkipper/Evaluation/RecapComparisonRunner.cs` | Runs configs, scores, renders the comparison. |
| `IntroSkipper/Evaluation/RecapMetrics.cs`, `RecapItemResult.cs`, `RecapMetricsSummary.cs`, `EvaluationReport.cs`, `RecapEvaluationCommand.cs` | + content-skip / missed-recap seconds + silent-miss / fired-but-wrong split. |
| `docs/recap-research/R2-scenarios.json` | Serialized snapshot of the dataset. |
| `IntroSkipper.Tests/TestRecapEnsemble.cs` | 21 tests: reconciliation, pipeline ordering, harm metrics, comparison relationships. |
