# Recap RFC C — Harden the current recap path

Status: draft / design spike with ship-ready prototype
Scope: keep the existing recap architecture (Chromaprint sting + black-frame boundary), fix its
conceptual bugs, and be honest about the accuracy ceiling of pure hardening.
Branch: `recap-rfc-c-harden`

---

## TL;DR verdict

The current recap detector is a thin layer bolted onto the introduction Chromaprint pass. It
shipped without any real end-to-end validation (PR #771 merged broken; commit `e17f044` "fix
recap" only added the missing `GetFingerprintRange` case that had been throwing
`ArgumentException`). Five of the six reported bugs are **confirmed**; one (the "duplicated clamp")
is **refuted as written** but points at a real, adjacent semantics problem that this RFC still
fixes.

Hardening meaningfully raises *precision and correctness* for the recap shape the architecture was
actually designed for — **a short shared "previously on" sting at/near the start of the episode,
optionally behind a cold open, bounded by a fade/black frame.** For that shape the prototype:

- stops forcing every recap to `0:00` and anchors the start to the cold open (bug 1),
- stops the end from overshooting into mid-episode scene cuts (bug 2),
- adds a real false-positive guard against the opening theme (bug 3),
- removes a wasteful duplicate audio decode (bug 5),
- and cleans up the window/clamp/semantics and hashing (bugs 4, 6).

What hardening **cannot** do is invent signals that aren't in the audio+black-frame data. The
honest ceiling: **recaps placed *after* the intro, recaps with no shared audio sting, recaps unique
to one episode, and "Previously on…" montages distinguished only by subtitles/cross-episode text
remain undetected** — and at least one (after-intro) is a placement the project wiki explicitly
lists as valid. See [§ Ceiling](#the-ceiling-what-pure-hardening-cannot-fix).

---

## What a recap is (model used here)

The "Previously on…" montage near the start of an episode. Per-episode the video/audio differ
*except* possibly a short shared sting ("Previously on <Show>") and sometimes a shared music bed.
Typically 10–60 s, often bookended by black-frame/fade transitions. Placement **varies**: before a
cold open, after a cold open, or after the intro. Surfaces as Jellyfin
`MediaSegmentType.Recap`.

The architecture exploits exactly one of those signals: the **shared sting**, found by the same
inverted-index Chromaprint search used for intros, plus black frames to bound the montage. Every
limitation below flows from that single design choice.

---

## Bug-by-bug analysis (confirm / refute + evidence + fix + test)

### Bug 1 — Recap is always forced to start at 0:00 — CONFIRMED

**Evidence (original 10.11 code):**

- `ChromaprintAnalyzer.GetEarliestTimeRange` zeroed the sting start: `if (lhsRecap.Start <= 5) { lhsRecap.Start = 0; }` (and the rhs twin). Original `ChromaprintAnalyzer.cs:317-325`.
- The operative path never used that start anyway: `BuildRecapFromChromaprintCandidateAsync`
  delegated to `ChapterAnalyzer.BuildRecapFromBlackFrames`, which **hardcodes** the start —
  `return new Segment(episodeId, new TimeRange(0, selectedBlackFrame.Time));`
  `ChapterAnalyzer.cs:275`.
- `TimeAdjustmentHelper.AdjustIntroTimesAsync` then snaps any start `<= EndSnapThreshold` (default
  `2.0`) to 0 — `TimeAdjustmentHelper.cs:71-89`. (This is *not* the main driver — black-frame build
  already returns 0 — but it would re-zero small non-zero starts.)

So a cold-open-before-recap episode `[cold open 0–40][recap 40–70][intro 70+]` was reported as
`[0–70]`, swallowing the cold open; an at-start recap was correct only by accident.

**Fix design (implemented):** preserve the true sting start through `GetEarliestTimeRange`
(`ChromaprintAnalyzer.cs:326-329`) and resolve the start structurally in
`RecapDetectionHelper.ResolveRecapStart`:

- sting start `<= 5 s` ⇒ recap opens the episode ⇒ start `0`;
- otherwise a cold open precedes it ⇒ anchor to the fade/black frame within a 6 s lead-in just
  before the sting, else to the sting start itself;
- gated behind the new `RecapAllowColdOpen` knob (default on) so the legacy `0:00` behavior is one
  toggle away.

`EndSnapThreshold = 2.0` no longer fights this: a 40 s anchored start is well above the snap
threshold and survives `TimeAdjustmentHelper`.

**Tests:** `ColdOpenBeforeRecap_AnchorsStartToStingNotZero`,
`ColdOpenBeforeRecap_AnchorsToFadeJustBeforeSting`, `RecapAtEpisodeStart_NoColdOpen_StartsAtZero`,
`AllowColdOpenDisabled_PreservesLegacyZeroStart`, `ResolveRecapStart_*`.

---

### Bug 2 — Boundary = "latest black frame before intro/max" overshoots or fails — CONFIRMED

**Evidence:** `ChapterAnalyzer.BuildRecapFromBlackFrames` selects the **maximum-time** qualifying
black frame (`if (selectedBlackFrame is null || blackFrame.Time > selectedBlackFrame.Time)`,
`ChapterAnalyzer.cs:264-266`) and returns `null` when none qualifies (`ChapterAnalyzer.cs:270-273`).

- **Overshoot:** with no intro detected, the window is `min(120, duration)`; a mid-episode scene
  cut at e.g. 110 s becomes the boundary, so `[0, 110]` swallows the episode opening.
- **Fail:** no black frame in range ⇒ `null` ⇒ no recap, even with a clear shared sting.
- It also conflates "minimum end **time**" with "minimum **duration**": the floor is applied to
  `blackFrame.Time` (`ChapterAnalyzer.cs:256`), never to `end − start`. With a non-zero start
  (bug 1's fix) that conflation would produce wrong-length recaps.

**Fix design (implemented):** for the Chromaprint path, `RecapDetectionHelper.SelectMontageEnd`
picks the **earliest** black frame *after the sting* that yields a recap whose **duration** is
within `[MinimumRecapDuration, MaximumRecapDuration]`. Earliest-valid (not latest) prevents
overshoot into later scene cuts; the duration floor skips fades too close to the start
(undershoot). No-black-frame is handled explicitly (see bug 3 fix): a long shared region with a
detected intro falls back to the shared body as the recap; otherwise detection refuses to guess.

The legacy `BuildRecapFromBlackFrames` (latest-frame, `0:00` start) is **intentionally retained**
for the black-frame-only fallback (`DetectRecapUsingBlackFrames`, default off) because that path has
no sting to anchor structure — see [§ What I deliberately did not change](#what-i-deliberately-did-not-change).

**Tests:** `MontageEnd_PicksEarliestValidFrame_NotLatest_AvoidingOvershoot`,
`MontageEnd_SkipsFramesShorterThanMinimumDuration`,
`NoBlackFrame_IntroDetected_LongSharedRegion_UsesSharedBodyAsRecap`,
`NoBlackFrame_IntroNotDetected_Rejects`.

---

### Bug 3 — False positive: "earliest shared region" may be the intro theme — CONFIRMED

**Evidence:** recap mode selects the earliest shared region
(`SelectSharedRegion → GetEarliestTimeRange`, `ChromaprintAnalyzer.cs:240-241`). The only guard was
`if (maximumBoundary <= card.End) return null;` (original `ChromaprintAnalyzer.cs:269`), where
`maximumBoundary` is clamped to `intro.Start` (`RecapDetectionHelper.GetMaximumBoundaryAsync`). That
guard **only works when the introduction was already detected and stored**. With intro detection
disabled/failed/first-episode-skipped, `maximumBoundary = min(120, duration)` and a shared *theme*
region `[30,50]` passes (`120 > 50`); the latest black frame at the theme's end then yields a recap
that is really the theme. The earliest shared region can also be a recurring studio ident / rating
bumper rather than a recap.

**Fix design (implemented):** `BuildChromaprintRecap` makes strictness depend on whether the intro
was detected (now returned alongside the boundary via `RecapScanWindow.IntroDetected`):

- **intro detected:** the intro-clamped window already excludes the theme; trust the candidate and
  use the black-frame montage end (or shared-body fallback).
- **intro NOT detected:** require a *short* sting — a shared region longer than
  `StingMaximumDuration = 20 s` is treated as the opening theme and rejected — **and** require a
  montage-end black frame (no shared-body fallback). A short shared sting corroborated by a montage
  fade still passes.

This is a heuristic, not a proof (see ceiling). It trades away long-shared-bed recaps when the
intro is unknown to avoid emitting the theme as a recap.

**Tests:** `IntroTheme_NoIntroDetected_LongSharedRegion_Rejected`,
`IntroTheme_IntroDetected_ClampedOutByScanWindow`,
`ShortStingWithMontageBoundary_NoIntroDetected_StillDetected`.

---

### Bug 4 — "Duplicated window-clamp logic (RecapDetectionHelper vs ChromaprintAnalyzer)" — REFUTED as written; real overlap fixed

**Evidence against the literal claim:** the window clamp `min(duration, MaximumRecapDetectionDuration)`
then `min(…, intro.Start)` lived in exactly **one** place — `RecapDetectionHelper.GetMaximumBoundaryAsync`
(original `RecapDetectionHelper.cs:21-36`) — and was *called* from both
`ChromaprintAnalyzer.cs:265` and `ChapterAnalyzer.cs:223`. There was no second inline copy in
`ChromaprintAnalyzer` on 10.11. (PR #771's intermediate revisions likely had one; it was already
extracted by merge time.)

**The real, adjacent problem (confirmed and fixed):** `MaximumRecapDetectionDuration` was doing
**double duty** — the candidate-duration cap in `GetMaximumSegmentDuration` (`ChromaprintAnalyzer.cs:244-253`)
*and* the scan-window ceiling in the helper — and the recap path mixes **two** near-identical knob
pairs (`Minimum/MaximumRecapDuration` vs `Minimum/MaximumRecapDetectionDuration`, both default
15/120) plus the hardcoded `RecapCardMinimumDuration = 3.0` (`ChromaprintAnalyzer.cs:26`). Each was
read in a different file with no single authority over "what is the recap window / what are the
duration bounds".

**Fix design (implemented):** `RecapDetectionHelper` is now the single home for the recap window and
boundary logic. The clamp is a pure function `ComputeMaximumBoundary(duration, maxDetection,
introStart?)` (`RecapDetectionHelper.cs`), wrapped by `GetRecapScanWindowAsync` which returns a
`RecapScanWindow(MaxBoundary, IntroDetected)`. The two knob pairs now have **documented, distinct
roles**: `Minimum/MaximumRecapDetectionDuration` bound the *scan window / earliest end time*;
`Minimum/MaximumRecapDuration` bound the *final segment duration*. Both analyzers consume the helper;
nothing re-derives the clamp.

**Tests:** `ComputeMaximumBoundary_ClampsToTightestBound` (theory, 4 cases).

---

### Bug 5 — Duplicate decode: recap re-fingerprints the identical opening audio — CONFIRMED

**Evidence:** `QueuedEpisode.GetFingerprintRange` returns the **same** range for both modes —
`Introduction => (0, IntroFingerprintEnd)` and `Recap => (0, IntroFingerprintEnd)`
(`QueuedEpisode.cs:147-148`). But the Chromaprint cache is keyed by `mode`
(`FFmpegService` cache read/write, and `ConfigHasher.DetectionCache(... Chromaprint, mode)` embeds
`{mode}` — `ConfigHasher.cs:78-79`). So the Recap pass missed the Introduction-written entry and
re-decoded the identical opening PCM — directly against the maintainer's "not CPU/GPU heavy"
constraint. The analyzer chain runs Introduction before Recap (`BaseItemAnalyzerTask.cs:74-80`), so
the intro fingerprint is reliably present when recap runs.

**Fix design (implemented):** `QueuedEpisode.GetFingerprintCacheMode` maps `Recap → Introduction`
(`QueuedEpisode.cs:163-164`); `FFmpegService.FingerprintAsync` computes the range from the real mode
but reads/writes the cache under the shared mode (`FFmpegService.cs:128-129`), and
`DetectionCacheService.HasCachedFingerprint` normalizes identically (`DetectionCacheService.cs:144-153`).
Result: the opening audio is decoded at most once per episode; recap reuses it. `DeleteByMode(Recap)`
still purges recap **black-frame** cache rows (those remain mode-keyed); the shared fingerprint stays
owned by Introduction, which is correct.

**Tests:** `GetFingerprintCacheMode_MapsRecapToIntroduction` (theory),
`RecapAndIntroduction_ShareTheSameFingerprintRange`,
`FingerprintAsync_Recap_ReusesIntroductionCacheEntry_NoDecode` (seeds only the Introduction entry in
a fake cache, asserts the Recap call hits it and never reads a Recap-keyed entry — no ffmpeg).

---

### Bug 6 — Naming/semantics/hash correctness — CONFIRMED (and guarded going forward)

**Evidence:**

- The "latest black frame" framing (PR #771: "pick the latest valid black frame, not the first") is
  itself the overshoot bug 2; "latest" was a deliberate choice that's wrong for a bounded montage.
- Two overlapping knob pairs + a magic `RecapCardMinimumDuration` const, addressed in bug 4.
- **Hash coverage:** the existing Recap hash (`ConfigHasher.cs:44-49`) did cover all *existing* recap
  knobs (`detMin/detMax`, `min/max`, `recapBlackFrames`, `bf*`, `pct/limit`, `fpbits/skip/shift`,
  chapter pattern, adjustment hash). The risk is **future** knobs silently not invalidating cached
  results.

**Fix design (implemented):** the new `RecapAllowColdOpen` knob is added to the Recap analysis hash
(`ConfigHasher.cs:48`, `|coldOpen=`), so toggling it re-analyzes. The cold-open thresholds
(`ColdOpenStartThreshold`, `ColdOpenLeadInWindow`, `StingMaximumDuration`) are compile-time consts —
they don't vary at runtime, so they correctly do **not** enter the hash. The new montage-end
selection consumes already-hashed knobs (`Minimum/MaximumRecapDuration`,
`MinimumRecapDetectionDuration`).

**Tests:** `RecapHash_ChangesWhenColdOpenToggleChanges`; existing
`RecapHash_ChangesWhenChromaprintTuningChanges` retained.

---

## Critical review — additional findings (file:line)

Findings beyond the six, surfaced while hardening. Not all are fixed in this prototype; flagged
honestly.

1. **No end-to-end test exists for recap.** Every recap test (`TestRecapDetection.cs`,
   `TestChapterAnalyzer.cs`) is synthetic and unit-level; nothing exercises
   `ChromaprintAnalyzer.AnalyzeMediaFiles` in recap mode against media. The broken-on-merge history
   is the direct consequence. This prototype adds more synthetic coverage but **cannot** close that
   gap without media fixtures. (Addressed partially: the new tests at least express the real
   *scenarios*, not just function plumbing.)

2. **Recap rides the Introduction analysis window.** `IntroFingerprintEnd` is derived from
   `AnalysisPercent`/`AnalysisLengthLimit` for *intros*. A recap that begins later than that window
   (e.g. after a long cold open in a long episode) is never fingerprinted. `QueuedEpisode.cs:147-148`.

3. **Per-pair commit, earliest-region only.** `GetEarliestTimeRange` (`ChromaprintAnalyzer.cs:301`)
   returns a single earliest region for a pair. If the earliest shared region is the theme/ident and
   a *later* region is the real sting, the detector commits to the wrong one and there is no
   "try the next region" path. The bug-3 guard rejects the theme but then yields **nothing** rather
   than the real recap.

4. **`maxBoundary <= card.End` over-rejects adjacent recaps.** When a recap ends *exactly* at the
   detected intro start, `MaxBoundary == card.End` is possible and the strict `<=` drops it. Rare,
   but it means a recap flush against the intro can be lost. `ChromaprintAnalyzer.cs:267-270`.

5. **`TimeAdjustmentHelper` is intro-shaped.** It applies `IntroStartOffset`/`IntroEndOffset`,
   chapter/silence/keyframe snapping (`TimeAdjustmentHelper.cs:92-131`) to recaps too. Silence-snap
   on the *end* is reasonable for a montage→episode fade, but `AdjustIntroBasedOnChapters` (default
   on) can pull a cold-open-anchored recap start onto an unrelated chapter mark. Left as-is
   (orthogonal), but worth a recap-specific adjustment profile later.

6. **Black-frame cache is per-analyzer-instance and per-episode only.** `_recapBlackFrameCache`
   (`ChromaprintAnalyzer.cs:33`) lives on the analyzer instance, which is recreated per mode per
   season (`BaseItemAnalyzerTask.cs:361-365`), so it does not span modes; the DB cache
   (`CacheEntryType.BlackFrame`, mode `Recap`) does the real cross-run de-dup. Fine, but the
   in-memory layer is nearly redundant.

7. **`Segment.Valid` is `End > 0` only** (`Segment.cs:82`). A recap legitimately starting at 0 with
   `End > 0` is valid; but a degenerate `[0,0]` from a failed build is correctly invalid. No bug —
   noting because the builder relies on it (`new Segment(episodeId)` sentinels at
   `ChromaprintAnalyzer.cs:123,127`).

---

## The ceiling — what pure hardening CANNOT fix

Hardening fixes *how we interpret* the two signals we have (shared sting + black frames). It cannot
manufacture signals. After every fix in this RFC, these real-world recap shapes remain **undetected
or unreliable**:

1. **Recaps placed *after* the intro.** The scan window is clamped to `intro.Start`
   (`ComputeMaximumBoundary`), so the searched range is `[0, introStart]`. A recap montage that runs
   *after* the intro theme is structurally outside the window and cannot be found. The project wiki
   explicitly lists "after the intro" as a valid placement, so this is a first-class miss, not an
   edge case. Closing it needs `intro.End` plumbed in plus a second scan window `[introEnd,
   introEnd + maxRecap]` — more decode, more false-positive surface, deliberately **out of scope**
   for "harden the current path."

2. **Recaps with no shared audio sting.** If a show's "Previously on…" has unique narration every
   episode and no shared music bed above the fingerprint threshold, Chromaprint finds nothing and no
   recap is emitted. Only the black-frame-only fallback (default off, `0:00` start, latest frame)
   could fire, and it cannot tell a recap from a cold open.

3. **Recaps unique to one episode / unmatched pairs.** Chromaprint needs ≥2 episodes sharing the
   sting (`ChromaprintAnalyzer.AnalyzeMediaFiles` pairs episodes). A season opener with a unique
   recap, or a recap present in only one episode, never matches.

4. **Recaps identifiable only by subtitles / on-screen "Previously on" text.** Cross-episode and
   subtitle signals — exactly the data this architecture does **not** read — are often the only
   reliable recap discriminator. Out of reach by construction.

5. **Long-sting recaps when the intro was not detected.** The bug-3 guard rejects shared regions
   longer than `StingMaximumDuration` (20 s) without a detected intro, to avoid emitting the theme.
   A genuine recap whose shared portion is a long music bed is collateral. Deliberate precision/
   recall trade-off.

6. **A repeated non-recap sting can still slip through.** A short studio ident / rating bumper that
   is byte-identical across episodes, followed by a fade, with no intro detected, satisfies the
   guard (short shared region + montage boundary) and could be emitted as a recap. The guard reduces
   this class; it does not eliminate it. Eliminating it needs corroboration the architecture lacks
   (chapter label, subtitle, cross-season consistency).

**Bottom line on accuracy:** for the "short shared sting near the start, optionally behind a cold
open, bounded by a fade" shape, this prototype turns a detector that produced *systematically wrong
boundaries* (always `0:00`, frequently overshooting) into one that produces *correct boundaries when
it fires* and *fires more conservatively*. That is a precision win and a correctness win, not a
coverage win — the set of recaps it can *see* is essentially unchanged (and is narrower by design
when the intro is unknown). The headline gaps (after-intro placement, no-shared-audio, single-episode
recaps, subtitle-only recaps) are unmovable without the cross-episode/subtitle signals this spike was
asked to assume absent.

---

## Prototype summary

### Behavior changes (all in the Chromaprint recap path)
- Non-zero, cold-open-aware recap start (`RecapAllowColdOpen`, default on).
- Earliest-valid montage-end selection with duration sanity (no overshoot/undershoot).
- Intro-aware false-positive guard (short-sting requirement + black-frame corroboration when no
  intro is detected; shared-body fallback only when an intro is detected).
- Single Introduction/Recap fingerprint cache entry (no duplicate decode).
- Centralized, pure scan-window clamp; documented knob roles; `RecapAllowColdOpen` in the hash.

### Files changed
- `IntroSkipper/Analyzers/RecapDetectionHelper.cs` — rewritten as the recap logic home
  (`ComputeMaximumBoundary`, `GetRecapScanWindowAsync`, `BuildChromaprintRecap`, `ResolveRecapStart`,
  `SelectMontageEnd`, `RecapScanWindow`, `RecapBuildContext`).
- `IntroSkipper/Analyzers/ChromaprintAnalyzer.cs` — uses the builder; stops zeroing the sting start.
- `IntroSkipper/Analyzers/ChapterAnalyzer.cs` — fallback uses `GetRecapScanWindowAsync`; legacy
  black-frame-only behavior retained.
- `IntroSkipper/Data/QueuedEpisode.cs` — `GetFingerprintCacheMode`.
- `IntroSkipper/FFmpeg/FFmpegService.cs`, `DetectionCacheService.cs` — shared fingerprint cache key.
- `IntroSkipper/Configuration/PluginConfiguration.cs`, `Helper/ConfigHasher.cs` — `RecapAllowColdOpen`
  + hash.
- `web/src/types.ts`, `web/src/tabs/analysis.ts` — UI for the new knob.
- `IntroSkipper.Tests/TestRecapHardening.cs` — new scenario tests.

### What I deliberately did not change
- `ChapterAnalyzer.BuildRecapFromBlackFrames` (and its 3 existing tests) — the black-frame-only
  fallback (`DetectRecapUsingBlackFrames`, default off). With no Chromaprint sting it has no
  structure to anchor a cold open, so it keeps `0:00` start + latest-frame. Its overshoot limitation
  is inherent and documented rather than papered over.
- `TimeAdjustmentHelper` (intro-shaped post-processing) — orthogonal; flagged in the review.
- After-intro scanning — out of scope (see ceiling §1).

### Build / test status
- `web`: `pnpm install --frozen-lockfile && pnpm build` — clean.
- `IntroSkipper`: builds warning-clean under `TreatWarningsAsErrors` + `AllEnabledByDefault`.
- `dotnet test`: 341 passed / 1 failed. The single failure is the pre-existing
  `TestAudioFingerprinting.TestSilenceDetection`, an ffmpeg silence-timestamp precision mismatch
  (differs at the 4th–5th decimal) present on the pristine 10.11 checkout — unrelated to recap.
  Baseline before this work was 315/1; this RFC adds 26 passing tests.
