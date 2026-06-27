# [Recap RFC D] Ensemble orchestration + ground-truth evaluation harness

**Status:** Draft / research spike — not for merge as-is.
**Branch:** `recap-rfc-d-ensemble-eval`
**Scope:** (A) how recap signals should *compose* on top of the existing analyzer chain, and (B) a working, media-free **measurement harness** so "the most effective recap detection" stops being a vibe and becomes a number. Part B is the shippable artifact; Part A is a design proposal.

> TL;DR — The current recap path is already a *primitive ensemble*: an ordered `List<IMediaFileAnalyzer>` where the first analyzer to write a segment wins (later ones skip via `NeedsAnalysis`). It has no notion of confidence, no boundary reconciliation, a per-season override (`AnalyzerAction.BlackFrame`) that is a **no-op for recap**, and two code paths that **assume the recap starts at 0 s** — which is wrong for two of the four real-world recap shapes. None of this was measurable before this spike. This RFC proposes a confidence-aware precedence design that fits the existing framework, and ships a deterministic evaluation harness (schema + seed dataset + metrics + runner + command + 29 tests) that lets spikes A/B/C be compared with evidence. **The harness proves the metric math; it does not prove real-world accuracy. That requires real labeled media we do not yet have.**

---

## 0. What a recap is (and why it's hard)

A recap is the "Previously on…" montage near the start of an episode: reused clips from earlier episodes, ~10–60 s, frequently bounded by black-frame/fade transitions. It surfaces to clients as Jellyfin `MediaSegmentType.Recap` (mapped in `IntroSkipper/Providers/SegmentProvider.cs:28`).

Placement varies. This RFC uses four **source shapes** as the unit of analysis because each one stresses the detectors differently:

| Shape | Recap location | Why it matters |
| --- | --- | --- |
| `RecapFirst` | starts at/near 0 s, before the cold open | The happy path; every current heuristic assumes this. |
| `ColdOpenThenRecap` | cold open first, then recap (start > 0 s) | Defeats "snap start to 0" logic. |
| `AfterIntro` | after the opening titles/OP | **Structurally unreachable** by the current search window (see §2, finding 6). |
| `NoRecap` | none | The false-positive surface. The metric that protects users. |

Multiple cheap signals can indicate a recap: chapter markers (`Recap`/`Previously`), subtitle phrases ("Previously on", "Last time"), a shared "previously on" audio sting, black-frame/fade structure, and cross-episode footage reuse. The whole game is **composing** them well and **knowing** whether the composition actually works.

---

## 1. The current recap chain, read as an ensemble

### 1.1 The orchestration primitive

`BaseItemAnalyzerTask` is the engine. Per run it builds an ordered mode list (Introduction, Credits, **Recap**, Preview, Commercial) at `IntroSkipper/ScheduledTasks/BaseItemAnalyzerTask.cs:74-80` and runs them sequentially (`:167-189`). For each mode it builds an ordered `List<IMediaFileAnalyzer>`:

- `ChapterAnalyzer` is **always** added first (`BaseItemAnalyzerTask.cs:324-328`).
- For Recap on a non-movie with a Chromaprint-capable ffmpeg, `ChromaprintAnalyzer` is appended (`BaseItemAnalyzerTask.cs:361-365`).

Then a per-season `AnalyzerAction` override can **promote** one analyzer to the front (`BaseItemAnalyzerTask.cs:372-390`, `PromoteAnalyzer` at `:469-478`), and the analyzers run in list order (`:394-398`). Each analyzer only touches episodes where `NeedsAnalysis(mode)` is true — i.e. not yet `Analyzed` and not `UserProvided` (`IntroSkipper/Data/QueuedEpisode.cs:131-132`). `ChromaprintAnalyzer` additionally early-returns if every queued episode is already analyzed (`IntroSkipper/Analyzers/ChromaprintAnalyzer.cs:44-51`).

**This *is* an ensemble.** The "combination rule" is: *ordered, first-valid-detection-wins, with a single per-season promotion knob.* Precedence is encoded purely as **list position**. There is no score, no vote, no merge.

### 1.2 The three recap signals available today

| Signal | Where it lives | Precision | Cost | Notes |
| --- | --- | --- | --- | --- |
| **Chapter marker** | `ChapterAnalyzer.FindMatchingChapter` (regex `ChapterAnalyzerRecapPattern`, `PluginConfiguration.cs:369-370`; SponsorBlock `recap` label, `ChapterAnalyzer.cs:53-55`) | Highest (explicit author/editor metadata) | Lowest (read container chapters) | Bounded by `MinimumRecapDuration`/`MaximumRecapDuration` (`ChapterAnalyzer.cs:291`). |
| **Black-frame structure** | `ChapterAnalyzer.DetectRecapUsingBlackFramesAsync` → `BuildRecapFromBlackFrames` (`ChapterAnalyzer.cs:221-273`), gated by `DetectRecapUsingBlackFrames` (default **false**, `PluginConfiguration.cs:276`) | Low (any early fade triggers it) | Medium (decode for `blackdetect`) | Picks the **latest** black frame in range, returns `[0, frame.Time]` (`ChapterAnalyzer.cs:272`). |
| **Audio sting + black frame** | `ChromaprintAnalyzer` recap path (`ChromaprintAnalyzer.cs:118-135`, `255-291`) | Medium (cross-episode shared sting) | Highest (fingerprint ≥2 episodes) | Earliest shared region ≥ `RecapCardMinimumDuration` 3 s (`:26`, `:221-222`), end refined to a black frame after the card (`:255-291`). |

### 1.3 Implied precedence today — and where it's wrong for recap

Reading the chain as an ensemble surfaces concrete defects (all with evidence):

1. **`AnalyzerAction.BlackFrame` is a no-op for recap.** The recap chain only ever contains `ChapterAnalyzer` and `ChromaprintAnalyzer` (`BaseItemAnalyzerTask.cs:361-365`). The promotion case looks for `BlackFrameAnalyzer or CreditsBlackFrameAnalyzer` (`:380-381`), which are **never added** for Recap. So a maintainer who sets the per-season action to BlackFrame for recap changes nothing. The UI exposes a lever wired to nothing.

2. **The black-frame signal has Chapter-tier priority, not last-resort priority.** Black-frame recap detection lives *inside* `ChapterAnalyzer` as a fallback after the regex misses (`ChapterAnalyzer.cs:79`, `:111-113`). Because `ChapterAnalyzer` runs first by default, a **low-precision** structural guess can mark an episode `Analyzed` and thereby **short-circuit** the medium-precision Chromaprint sting match (which then early-returns, `ChromaprintAnalyzer.cs:48-51`). The cheap-but-noisy signal pre-empts the more specific one — backwards for precision.

3. **No confidence anywhere.** `Segment.Valid` is literally `End > 0.0` (`IntroSkipper/Data/Segment.cs:82`). There is no per-signal score, so the ensemble cannot prefer a high-confidence cheap signal over a low-confidence expensive one except by static list order. "First wins" is the *only* resolution rule.

4. **No boundary reconciliation.** Each analyzer writes its own segment via `UpdateTimestampAsync`; whoever marks `Analyzed` first wins outright. If chapters say `[0, 30]` and chromaprint+blackframe say `[0, 34]`, the loser is silently dropped — no merge, no agreement check. End-boundary refinement via black frames is applied only inside the Chromaprint path (`ChromaprintAnalyzer.cs:255-291`), **not** to chapter matches, so boundary semantics differ by signal.

5. **Recall is hostage to a shared sting.** Chromaprint recap needs ≥2 episodes that share the *same* audio (`ChromaprintAnalyzer.cs:44-51`, `GetEarliestTimeRange` `:293-328`). Shows whose recaps are unique per episode (different clips, different VO) expose only a short shared jingle — and if there isn't one, Chromaprint finds nothing. This is the structural argument for adding subtitle / cross-episode-footage signals (spikes A/B).

6. **`AfterIntro` recaps are structurally undetectable.** Recap mode runs *after* Introduction (`BaseItemAnalyzerTask.cs:74-80`), and `RecapDetectionHelper.GetMaximumBoundaryAsync` caps the recap search window at the detected intro start (`IntroSkipper/Analyzers/RecapDetectionHelper.cs:32`). Both the black-frame fallback and the Chromaprint candidate respect that cap (`ChromaprintAnalyzer.cs:265`, `:269`). A recap located *after* the opening titles is outside `[0, intro.Start]`, so it cannot be found whenever an intro is detected. The current design simply cannot serve this shape.

7. **Both detectors bias the start to 0 s.** `BuildRecapFromBlackFrames` always returns `[0, frame.Time]` (`ChapterAnalyzer.cs:272`); `GetEarliestTimeRange` snaps the start to 0 when it is ≤5 s (`ChromaprintAnalyzer.cs:317-325`). Correct for `RecapFirst`, wrong for `ColdOpenThenRecap`. This inflates start-boundary error on any shape whose recap does not begin at 0.

8. **It shipped broken with no end-to-end test.** PR #771 ("Add optional recap detection fallback to early black frame", merged 2026‑06‑19, commit `1f9b88e`) is AI-authored (Copilot) and its body claims the Recap fingerprint range was added in **`FFmpegWrapper.cs` line 135** — a file that **does not exist** in this repo (`git ls-files | grep FFmpegWrapper` → empty; the range lives in `QueuedEpisode.GetFingerprintRange`). The real code lacked an `AnalysisMode.Recap` case, so recap fingerprinting threw `ArgumentException("Unknown analysis mode Recap")` and aborted analysis until the 2026‑06‑27 "fix recap" commit (`e17f044`) added `AnalysisMode.Recap => (0, IntroFingerprintEnd)` at `QueuedEpisode.cs:148`. That a feature could merge and sit broken is the strongest possible argument for this harness.

---

## 2. Proposed ensemble / orchestration design

The design **keeps the existing framework**: ordered `IMediaFileAnalyzer` list + `NeedsAnalysis` short-circuit + `AnalyzerAction` promotion + per-mode `ConfigHasher`. It adds (a) explicit precedence tiers, (b) a confidence concept, and (c) deterministic conflict/boundary rules. It does **not** invent a new pipeline.

### 2.1 Precedence tiers (ordered by precision, then cost)

```
Tier 1  Chapters        highest precision, lowest cost   -> ChapterAnalyzer (regex/SponsorBlock only)
Tier 2  Subtitles       high precision,    low cost      -> SubtitleRecapAnalyzer  (spike A, NEW)
Tier 3  Sting+blackframe medium precision, medium cost   -> ChromaprintAnalyzer recap path (existing)
Tier 4  Cross-episode    lower precision,  highest cost   -> ReuseRecapAnalyzer (spike B, NEW)
```

Rationale: order by **precision first** (a high-precision signal should win when present) and **cost second** (cheap signals run first so the expensive ones are skipped on the majority of episodes that an earlier tier already resolved). This is exactly what the chain's "first valid wins + later analyzers skip `Analyzed` episodes" already does — we are *naming* the tiers and fixing their order, not replacing the mechanism.

**Key correction vs. today:** the black-frame structural signal must be demoted from "inside Tier 1" to an *end-boundary refiner* shared by all tiers (see §2.3), or to its own explicit last-resort tier — **not** carried at Chapter priority (finding 2). And `AnalyzerAction.BlackFrame` must either gain a real standalone recap black-frame analyzer or be hidden from the recap UI (finding 1).

### 2.2 What "confidence" means here, and whether `Segment` must carry it

Confidence = *the probability that this interval is a correct recap*, used to (i) order tiers and (ii) resolve disagreements. Two implementation levels:

- **Level 0 (today, no schema change): confidence is implicit in tier order.** "First valid detection wins" ≈ "highest-precedence available signal wins." This is faithful to the current model and needs zero changes to `Segment`. It cannot, however, let a *later, higher-confidence* signal override an *earlier, lower-confidence* one, nor expose confidence to the UI/eval.
- **Level 1 (proposed upgrade): add `double Confidence` to `Segment`.** This changes the combination rule from "first wins" to "highest-confidence wins (ties broken by tier)," enables agreement boosting (§2.3), and lets the harness weight/threshold on confidence. Cost: `Segment` is a `[DataContract]` persisted to the DB (`IntroSkipper/Data/Segment.cs`), so this is a schema + migration touch (cf. the existing `AddConfigHashes`/`AddIsUserProvided` migrations). **Recommendation: stay at Level 0 for the first shippable recap ensemble; adopt Level 1 only once the harness shows that conflict resolution (not recall) is the bottleneck.** Don't pay for a migration until the numbers justify it.

### 2.3 Conflict & boundary resolution rules (deterministic)

When more than one enabled signal produces a candidate for the same episode:

1. **Winner of the *interval* = highest precedence tier that fired** (Level 0) or **highest confidence** (Level 1). Deterministic, no averaging of unrelated guesses.
2. **End boundary may be refined by the black-frame/silence structural signal** within a small window (generalize the existing Chromaprint end-refinement at `ChromaprintAnalyzer.cs:255-291` and the `TimeAdjustmentHelper` silence/keyframe snapping to *every* tier, not just chromaprint). This fixes finding 4's inconsistency.
3. **Start boundary: trust the explicit signal; never blanket-snap to 0.** Replace the unconditional `[0, …]` (`ChapterAnalyzer.cs:272`) and the `Start ≤ 5 → 0` snap (`ChromaprintAnalyzer.cs:317-325`) with "snap to the nearest black frame/silence within a window," so `ColdOpenThenRecap` keeps a correct non-zero start (fixes finding 7).
4. **Agreement signal.** If two independent tiers overlap with high IoU, treat it as corroboration (raise confidence at Level 1; at Level 0 just prefer the higher tier). If they disagree (low IoU), keep the higher tier's interval but the harness will show the disagreement as boundary error.
5. **Search window must not assume "before intro."** To serve `AfterIntro`, `GetMaximumBoundaryAsync` (`RecapDetectionHelper.cs:32`) needs an "after-intro" branch (search `[intro.End, intro.End + MaximumRecapDetectionDuration]`) gated by a flag — otherwise that shape is unservable (finding 6).

### 2.4 Per-signal enable flags + config hashing

Each tier gets an independent enable flag, composed into the recap config hash so toggling any signal invalidates cached recap results. Today `ConfigHasher.Analysis(…, AnalysisMode.Recap, …)` already folds in the chapter pattern, `DetectRecapUsingBlackFrames`, black-frame params, and Chromaprint tuning (`IntroSkipper/Helper/ConfigHasher.cs:44-49`). The ensemble extends this set:

| Tier | Enable flag (proposed) | Already hashed? | AnalyzerAction |
| --- | --- | --- | --- |
| Chapters | `ScanRecap` + chapter pattern | yes (`ConfigHasher.cs:45`) | `Chapter` |
| Subtitles | `DetectRecapUsingSubtitles` (new) + phrase list | **add to recap hash** | `Subtitle` (new enum value) |
| Sting+blackframe | implicit (Chromaprint tuning) + `DetectRecapUsingBlackFrames` | yes (`ConfigHasher.cs:47-48`) | `Chromaprint` / fixed `BlackFrame` (finding 1) |
| Cross-episode reuse | `DetectRecapUsingCrossEpisodeReuse` (new) + reuse params | **add to recap hash** | (new) |

So composing signals = appending their flags+params to the recap branch of `ConfigHasher` and (optionally) extending the `AnalyzerAction` enum (`IntroSkipper/Data/AnalyzerAction.cs:10-36`). No framework change.

### 2.5 Signal → analyzer → config → tier map (the whole proposal on one page)

```
Tier 1  Chapter     ChapterAnalyzer(regex+SponsorBlock)  flag ScanRecap+pattern   action Chapter      conf HIGH
Tier 2  Subtitle    SubtitleRecapAnalyzer (NEW, spike A) flag DetectRecapUsingSubtitles  action Subtitle (NEW)  conf HIGH/MED
Tier 3  Sting+BF    ChromaprintAnalyzer recap path       Chromaprint tuning + BF   action Chromaprint  conf MED
Tier 4  Reuse       ReuseRecapAnalyzer (NEW, spike B)    flag DetectRecapUsingCrossEpisodeReuse        conf LOW/MED
Shared  BF/silence  TimeAdjustmentHelper end-refine      BF params                 (refiner, all tiers)
```

---

## 3. The evaluation harness (Part B — the build)

Everything below is implemented, compiles under the repo's strict analyzer set (`TreatWarningsAsErrors` + `AllEnabledByDefault`), and is covered by 29 deterministic tests. Code lives in `IntroSkipper/Evaluation/` as **`internal` types** (reachable from tests via the existing `InternalsVisibleTo IntroSkipper.Tests`). **No production analysis path was modified** — the harness is inert unless explicitly invoked.

> Placement note: the metric core has **zero dependency** on Jellyfin or on the plugin's analysis types (it uses its own `RecapInterval`, not `Segment`/`TimeRange`). If maintainers prefer zero production footprint, the entire `Evaluation/` folder can be moved to the test project or a dedicated `IntroSkipper.Evaluation` tool with a one-line namespace change.

### 3.1 Dataset schema

A labeled dataset is JSON (`RecapDataset` / `RecapLabel`):

```jsonc
{
  "version": 1,
  "labels": [
    {
      "series": "Cold Harbor",          // join key (case-insensitive, trimmed)
      "season": 2,
      "episode": 2,
      "hasRecap": true,                  // ground truth: does this episode have a recap?
      "recapStart": 52.0,               // seconds; ignored when hasRecap == false
      "recapEnd": 88.0,                 // seconds
      "sourceShape": "ColdOpenThenRecap",// RecapFirst | ColdOpenThenRecap | AfterIntro | NoRecap | Unknown
      "notes": "Cold open to ~52s, then recap. Recap does NOT start at 0."
    }
  ]
}
```

Detections produced by an analysis run use the parallel `RecapDetectionSet` / `RecapDetection` shape:

```jsonc
{
  "version": 1,
  "detections": [
    { "series": "Cold Harbor", "season": 2, "episode": 2,
      "detected": true, "detectedStart": 0.0, "detectedEnd": 34.0, "signal": "blackframe" }
  ]
}
```

Labels and detections join on `RecapEpisodeKey.For(series, season, episode)` (series upper-cased invariantly + trimmed, so trivial casing/whitespace differences still match).

### 3.2 Seed dataset & contributing real labels

`docs/recap-research/seed-dataset.json` ships 14 **synthetic** entries spanning every shape (4 `RecapFirst`, 3 `ColdOpenThenRecap`, 2 `AfterIntro`, 5 `NoRecap`; 9 with-recap / 5 no-recap) across three fictional series. It is deliberately synthetic — it exercises the metric math and the per-shape breakdown, **not** real-world accuracy (§5).

To contribute **real** labels, a user adds entries with the true `series/season/episode`, watches the episode, records `recapStart`/`recapEnd` and the `sourceShape`, and sets `hasRecap`. Real boundaries can be lifted straight from the segment editor (`SegmentEditorController`) or read from the plugin DB. No code change is needed to grow the dataset — it is just JSON.

### 3.3 Metric definitions (exact)

All metrics are pure geometry over `RecapInterval` (`RecapMetrics.cs`). For detected `d` and truth `t`:

- **Intersection** `I(d,t) = max(0, min(d.end,t.end) − max(d.start,t.start))` (0 if either is empty).
- **Union** `U(d,t) = dur(d) + dur(t) − I(d,t)`.
- **IoU** `= I/U` (defined 0 when `U ≤ 0`, so a miss scores 0, never NaN).
- **Match** at threshold τ: `d.HasValue && t.HasValue && IoU ≥ τ` (default τ = 0.5).

Per-episode classification (`RecapItemResult`), given a firing detection (`d.HasValue`) and the `hasRecap` label:

| | detected & IoU ≥ τ | detected & IoU < τ, or no detection |
| --- | --- | --- |
| **hasRecap** | True Positive | False Negative |
| **noRecap** | False Positive *(fired)* | True Negative *(silent)* |

Aggregates (`RecapMetricsSummary`), with **NaN for undefined rates** (zero denominator) so "0 %" is never confused with "not measurable":

- **Detection rate (recall)** `= TP / (TP + FN) = TP / withRecap`.
- **False-positive rate** `= FP / (FP + TN) = FP / withoutRecap`.
- **Precision** `= TP / (TP + FP)`; **F1** = harmonic mean of precision and recall.
- **Start MAE / End MAE** = mean `|d.start − t.start|` / `|d.end − t.end|` over episodes that are **hasRecap *and* fired** (independent of τ, so a poorly localized hit still contributes its error — `BoundaryCount` reports the sample size).
- **Mean IoU** = mean IoU over **all** hasRecap episodes (a miss contributes 0), so it captures localization *and* recall in one number.

Every formula above has a dedicated unit test with hand-computed expectations (`IntroSkipper.Tests/TestRecapEvaluation.cs`).

### 3.4 The runner & report

`RecapEvaluator.Evaluate(dataset, detections, options)` joins detections to labels, scores each episode, and returns an `EvaluationReport` with an **aggregate** block, a **per-shape** breakdown, the raw per-episode results, and an `UnmatchedDetections` count (detections with no label). `EvaluationReport.Format()` renders Markdown:

```
# Recap detection evaluation
IoU match threshold: 0.5

## Aggregate
| metric | value |
| --- | --- |
| detection rate (recall) | 0.500 (2/4) |
| false-positive rate     | 0.500 (1/2) |
| precision               | 0.667 |
| start MAE (s)           | 16.67 (n=3) |
| end MAE (s)             | 20.00 (n=3) |
| mean IoU                | 0.417 |
...
## Per shape
| shape | n | withRecap | recall | fpRate | precision | startMAE | endMAE | meanIoU |
| RecapFirst        | 2 | 2 | 1.000 | n/a   | 1.000 | 0.00  | 5.00  | 0.833 |
| ColdOpenThenRecap | 1 | 1 | 0.000 | n/a   | n/a   | 50.00 | 50.00 | 0.000 |
...
```

### 3.5 File map

| File | Role |
| --- | --- |
| `IntroSkipper/Evaluation/RecapInterval.cs` | Pure `[start,end]` value type (decoupled from `Segment`/`TimeRange`). |
| `IntroSkipper/Evaluation/RecapMetrics.cs` | Intersection / union / IoU / boundary error / match. |
| `IntroSkipper/Evaluation/RecapSourceShape.cs` | The four shapes (+ `Unknown`). |
| `IntroSkipper/Evaluation/RecapLabel.cs`, `RecapDataset.cs` | Ground-truth schema + JSON load/save. |
| `IntroSkipper/Evaluation/RecapDetection.cs`, `RecapDetectionSet.cs` | Detector-output schema + `FromInterval` adapter. |
| `IntroSkipper/Evaluation/RecapEpisodeKey.cs` | Normalized join key. |
| `IntroSkipper/Evaluation/RecapClassification.cs`, `RecapItemResult.cs` | Per-episode TP/FP/FN/TN + errors. |
| `IntroSkipper/Evaluation/RecapMetricsSummary.cs` | Aggregate metric block (NaN-safe). |
| `IntroSkipper/Evaluation/RecapEvaluator.cs` | The runner. |
| `IntroSkipper/Evaluation/EvaluationReport.cs`, `EvaluationOptions.cs` | Report + Markdown renderer + options. |
| `IntroSkipper/Evaluation/RecapEvaluationCommand.cs` | Thin opt-in `Execute(args, writer)` entry point. |
| `IntroSkipper/Evaluation/RecapEvaluationJson.cs` | Cached `JsonSerializerOptions`. |
| `docs/recap-research/seed-dataset.json` | 14-entry synthetic seed. |
| `IntroSkipper.Tests/TestRecapEvaluation.cs` | 29 deterministic tests. |

### 3.6 How to run it

**As tests (no media):**
```
dotnet test IntroSkipper.Tests/IntroSkipper.Tests.csproj -p:SkipWebBuild=true --filter "FullyQualifiedName~TestRecapEvaluation"
```

**As a command (file in → report out):** `RecapEvaluationCommand.Execute(args, writer)` accepts
`--truth <labels.json> --detections <detections.json> [--iou <0..1>] [--json]` and returns `0` (ok), `2` (usage error), or `1` (runtime error). It is invoked directly from tests today; a 3-line console `Main` or a Jellyfin scheduled-task wrapper can call the same method unchanged.

### 3.7 Wiring to a real analysis run

The harness is fed canned detections in tests. To score a **real** run, export one `RecapDetection` per labeled episode from the plugin DB and run the command:

```csharp
// For each labeled (series, season, episode) resolved to its Jellyfin itemId:
var segments = await Plugin.GetSegmentsAsync(itemId, ct);          // IntroSkipper/Plugin.cs:326
var recap = segments.FirstOrDefault(s => s.Type == AnalysisMode.Recap)?.ToSegment();
set.Detections.Add(RecapDetection.FromInterval(
    series, season, episode,
    recap is { Valid: true } ? new RecapInterval(recap.Start, recap.End) : RecapInterval.Empty,
    signal: "current-chain"));
// write set.Serialize() to detections.json, then:
//   RecapEvaluationCommand.Execute(["--truth","labels.json","--detections","detections.json"], Console.Out)
```

`Detected` maps to `Segment.Valid` and start/end map directly. That single adapter (`RecapDetection.FromInterval`) is the only seam between the production analyzers and the harness.

---

## 4. How the team compares approaches A / B / C with this

The harness makes the spikes commensurable:

1. **Fix one label set.** Everyone scores against the *same* `labels.json` (grow it with real episodes over time).
2. **Each spike emits a `detections.json`** from its branch via the §3.7 adapter (subtitles, cross-episode reuse, hardened current path).
3. **Run the command per spike**, diff the reports. Compare: detection rate (recall), false-positive rate, start/end MAE, mean IoU — *and the per-shape rows*, which is where the interesting differences live (e.g. does the subtitle signal finally make `AfterIntro` non-zero?).
4. **Ablate the ensemble.** Because each tier has an enable flag folded into the config hash (§2.4), you can score "chapters only", "chapters+subtitles", "all four" and read off each signal's marginal contribution to recall and its marginal cost in false positives. That is exactly the evidence needed to choose a default precedence.

A signal that raises recall but also raises the false-positive rate on `NoRecap` is a *worse* default than its recall alone suggests — the harness makes that trade explicit instead of leaving it to intuition.

---

## 5. What this harness can and cannot tell us (the honest part)

**It can:**
- Prove the metric math is correct (29 deterministic tests; IoU/MAE/FP/recall/per-shape all hand-checked).
- Rank detector outputs **on a given label set** with reproducible, environment-independent numbers.
- Localize *where* a detector fails by shape (the per-shape table is the diagnostic).
- Catch a regression of the PR #771 class (a detector that silently produces nothing scores recall 0 — a red light, not a green merge).

**It cannot (today):**
- **Prove real-world accuracy.** The seed data is *synthetic*. Synthetic labels are not drawn from the real distribution of shows/genres/editing styles, and synthetic detections are not real detector behavior. Green metrics here mean "the arithmetic is right," **not** "recap detection works on your library."
- **Validate ffmpeg-dependent behavior.** Black-frame/sting detection depends on real decoding. This spike deliberately does not touch that — and the repo already demonstrates the hazard: `TestAudioFingerprinting.TestSilenceDetection` fails on this machine purely because the local ffmpeg's `silencedetect` rounds to slightly different 4th–5th-decimal values than the hardcoded expectations. Real-media tests are brittle across environments; the metric harness is not, *because* it is media-free. That separation is a feature, but it is also the boundary of what the harness certifies.
- **Choose the τ threshold or the "good enough" boundary tolerance for you.** τ = 0.5 IoU is a convention. For the *skip-button* UX, end-boundary error probably matters more than IoU (a few seconds late is worse than a few seconds early). The harness reports the inputs to that decision; it does not make it.

**What real validation needs:** a labeled corpus of real episodes — order ~30–50 per shape per major genre (live-action drama, procedural, anime), including a healthy `NoRecap` majority to make the false-positive rate meaningful, ideally contributed by multiple users to avoid one person's library biasing the result. Until that exists, every accuracy claim about recap detection — including this RFC's — is a hypothesis, not a result.

---

## 6. Appendix — baseline & status

- **Baseline before this spike:** 315 tests pass, 1 fails (`TestAudioFingerprinting.TestSilenceDetection`, the ffmpeg-precision flake above).
- **After this spike:** 344 pass, same 1 pre-existing failure. The 29 new tests are all green; no production code path was modified; the strict analyzer build stays warning-clean.
- **Build/test:** `cd web && pnpm install --frozen-lockfile && pnpm build` then `dotnet test IntroSkipper.Tests/IntroSkipper.Tests.csproj -p:SkipWebBuild=true`.
