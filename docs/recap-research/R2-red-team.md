# R2 — Adversarial red-team of the Round-1 recap research

**Role:** skeptical reviewer of RFCs A/B/C/D and Captain's proposed tiered ensemble. **Goal:** find
where Round 1 is wrong, over-optimistic, or unvalidated — not to bless it.

**Reference convention:** `path:lines` with no qualifier = the `10.11` branch (where this doc lives).
Prototype files that exist only on a feature branch are tagged `(branch <name>)`. RFC docs are quoted
by section.

**What I actually did:** read all four RFC docs and their prototype code on the four branches
(`recap-rfc-a-subtitles`, PR #807 → `b-cross-episode`, `recap-rfc-c-harden`,
`capy/recap-rfc-ensemble-eval`), the shipped recap path on `10.11`, the recap-fix commit `e17f044`,
and PR #798's entropy/saturation plumbing on `capy/credits-nonblack`. I independently **built and
ran** code, not just read it:

- RFC B's 8 reuse tests **pass** (incl. the `FindContiguous` deep-reuse demo) and I reproduced the
  benchmark on this VM: **4.5–7.4 ms/pair**, **3–4 distinct shifts** — numbers that, as I argue in
  §1.6, are an artifact of the synthetic input, not evidence of real-world cost/quality.
- The baseline failure all four RFCs cite is **real and exactly as described**:
  `TestAudioFingerprinting.TestSilenceDetection` fails on system ffmpeg 6.1.1 with a precision diff
  (`44.631042` expected vs `44.631` actual). That confirms their honesty *and* D's point that
  real-media tests are environment-brittle.
- Test-count claims are honest when counted as xUnit cases (A = 58 + 1 spike; C = 26; D = 29) — I
  verified by counting `[Fact]`/`[Theory]`/`[InlineData]`. **All are synthetic/unit-level**, which is
  the whole problem (§1.1).

Bottom line up front: **the engineering in Round 1 is mostly honest and individually competent. The
*synthesis* is not yet trustworthy** — it is presented as more validated, and more internally
coherent, than it is. The single most important sentence in this review: **nothing here has run
against one frame of real recap media, and the harness that is supposed to fix that cannot measure
any detector's actual signal.** Until a real labeled corpus exists, every accuracy claim — including
Captain's — is a hypothesis.

---

## 0. Severity-ranked summary

| # | Objection | Severity | Certainty |
|---|---|---|---|
| 1.1 | Zero real-media validation; D's harness measures interval arithmetic, **not** detectors; seed data can't stress any signal | **Blocker** | Certain |
| 1.2 | Harness buckets don't model user harm: "skipped the cold open" scores **identically** to "silent miss" | **Blocker** | Certain (code-verified) |
| 1.3 | B-vs-C **cache-mode conflict** is real and silent; adopting B's recommended integration breaks C's dedup *and* Introduction detection | **Blocker** | Certain (code-verified) |
| 1.4 | A and C both rewrite recap start, differently; D's "shared reconciliation" misses the actual residual corruptor (`AdjustIntroBasedOnChapters`) | High | Certain (code-verified) |
| 1.5 | C's earliest-shared-region selection (unchanged) **undermines C's own cold-open fix** | High | Certain (code + C's own finding #3) |
| 1.6 | B's cost/quality numbers are on **uniform-random** fingerprints that don't resemble chromaprint; the "bug" is a non-defect in shipped behaviour | High | Certain (reproduced) |
| 1.7 | A's "highest precision" is overstated; anchoring doesn't stop in-dialogue openers; JP/KO defaults are bare high-frequency words | Medium-High | Certain (code-verified) |
| 1.8 | **AfterIntro is detected by no Round-1 prototype**; D's after-intro window is pure speculation; A forecloses the one path that could reach it | High | Certain |
| 1.9 | Real per-signal coverage is unquantified; large slices of a real library still yield **nothing or wrong** output | High | Argued |
| 5 | Visual entropy/saturation (PR #798) is the **weakest** recap signal — structurally incapable on content, low-precision on cards, worst-region FP | Medium | Certain (code-verified) |

---

## 1. Prioritized objections

### 1.1 — The synthesis is unvalidated, and "measured by D's harness" is misleading *(Blocker)*

Captain's framing is "a tiered ensemble … measured by D's harness." That conflates two very
different things. **D's harness does not measure detectors.** It scores a list of intervals you hand
it against a list of ground-truth intervals — pure geometry over `RecapInterval`
(`IntroSkipper/Evaluation/RecapMetrics.cs:52-61`, branch `capy/recap-rfc-ensemble-eval`). It has, by
design, **zero** dependency on Jellyfin, `Segment`, audio, frames, or subtitles (D §3 / §3.5). The
seed dataset is 14 synthetic interval labels (`series/season/episode/hasRecap/recapStart/recapEnd/
sourceShape`) with **no subtitle text, no audio, no black frames**
(`docs/recap-research/seed-dataset.json`, branch `capy/recap-rfc-ensemble-eval`).

Consequences the synthesis glosses over:

- You **cannot** run A's phrase matcher, B's audio reuse, or C's black-frame logic through this
  harness. There is no media to feed them. The §3.7 "wiring to a real run" adapter only exports
  whatever segment the plugin already wrote — so the harness can score a detector solely *after* it
  has run on real media that **does not exist in this repo.**
- D is explicit and honest — "**The harness proves the metric math; it does not prove real-world
  accuracy. That requires real labeled media we do not yet have.**" (D §0); "Green metrics here mean
  'the arithmetic is right,' **not** 'recap detection works on your library.'" (D §5). The problem is
  not D's honesty; it is that **the synthesis leans on "measured by D's harness" as if that were
  validation.** It is not.

So every comparative claim in Round 1 — A "highest precision," B "the true model," C "precision
win" — rests on synthetic unit tests and self-description. Each RFC reports "all my new tests pass,"
which I confirmed is true and which proves only that the code does what its author asserted on inputs
its author chose. None of that survives contact with a real library. **Nothing should default on, and
B should not be wired in at all, until §6's protocol produces numbers.**

### 1.2 — The harness cannot tell "skipped real story" from "did nothing" *(Blocker)*

The task asks whether the metrics capture what users feel — "a recap skipped 5 s early vs a cold open
wrongly skipped." **They do not, and the classification actively hides the worst case.**

`RecapItemResult.Classify` (`IntroSkipper/Evaluation/RecapItemResult.cs:72-80`, branch
`capy/recap-rfc-ensemble-eval`):

```csharp
if (hasRecap)
{
    return fired && matched ? RecapClassification.TruePositive : RecapClassification.FalseNegative;
}
return fired ? RecapClassification.FalsePositive : RecapClassification.TrueNegative;
```

`matched` is `IoU ≥ τ` (default 0.5) (`RecapMetrics.cs:89-90`). Take the scenario the dataset itself
contains — `ColdOpenThenRecap`, truth `[52, 88]` (`seed-dataset.json` Cold Harbor S2E2). Two
detectors:

- Detector X stays **silent** → `fired=false` → **FalseNegative**.
- Detector Y fires **`[0, 34]`** (the current code's behaviour: start forced to 0, end at an early
  fade) → IoU(`[0,34]`,`[52,88]`) = 0 → `matched=false` → **FalseNegative**.

**Same bucket.** But Y tells the client to skip `[0, 34]` — the **cold open, i.e. real story**. X does
nothing. The harness rates them equally and reports identical per-shape recall. IoU and MAE are
symmetric; neither encodes the asymmetry that *skipping content the user wanted is far worse than
missing a recap the user would have skipped.* Boundary error is only recorded when `hasRecap && fired`
(`RecapItemResult.cs:30-34`), so Y's harm surfaces only as a large `startMAE`, lumped with benign
localization error.

D half-acknowledges this — "For the skip-button UX, end-boundary error probably matters more than
IoU … The harness reports the inputs to that decision; it does not make it." (D §5) — but ships no
asymmetric or content-skip metric, and no separation of "silent miss" from "confidently wrong fire."
**A metric set that green-lights a story-skipping detector as merely "low recall" is not fit to gate a
merge.** Fix in §6.

### 1.3 — B silently breaks C's fingerprint-cache assumption *(Blocker; attack #2)*

C's headline "no duplicate decode" fix maps the Recap fingerprint onto the Introduction cache entry:
`QueuedEpisode.GetFingerprintCacheMode(Recap) ⇒ Introduction` (branch `recap-rfc-c-harden`), and
`FFmpegService.FingerprintAsync` computes the *range* from the real mode but reads/writes the cache
under the **shared** mode (branch `recap-rfc-c-harden`):

```csharp
var (start, end) = episode.GetFingerprintRange(mode);        // Recap ⇒ (0, IntroFingerprintEnd)
var cacheMode = QueuedEpisode.GetFingerprintCacheMode(mode);  // ⇒ Introduction
return FingerprintAsync(episode, cacheMode, start, end, cancellationToken);
```

This works **only because** `GetFingerprintRange(Introduction)` and `GetFingerprintRange(Recap)` are
**both** `(0, IntroFingerprintEnd)` on `10.11` (`IntroSkipper/Data/QueuedEpisode.cs:147-148`), and the
cache key compares `Start`/`End` with `==` (C's `DetectionCacheService` change keys on
`e.Start == start && e.End == end`). C even documents the fragility: `GetFingerprintRange` is "the
**single source of truth** for detection cache keys … the cached Start/End doubles always round-trip
bit-exactly" (`QueuedEpisode.cs:134-139`).

Now read B's recommended integration. B §8: *"(b) **promote Introduction to fingerprint the full
episode** and have both Introduction and Recap read the same `(0, Duration)` entry … the most
cost-efficient integration."* If that lands:

1. **C's dedup dies silently.** Introduction now writes `(Introduction, 0, Duration)`. C's recap path
   still asks for `(0, IntroFingerprintEnd)` under `mode=Introduction`. `IntroFingerprintEnd ≠
   Duration`, so the `==` key **misses**, recap **re-decodes**, and C's Bug-5 fix is undone with no
   error and no log. Two entries now coexist under `mode=Introduction` with different `(Start,End)`.
2. **C's recap *matching* breaks if you instead force Recap onto `(0, Duration)`.** The shipped/
   hardened Chromaprint recap assumes the fingerprint is the **opening only** and takes the *earliest*
   shared region (`IntroSkipper/Analyzers/ChromaprintAnalyzer.cs:293-328`). Over a full-episode
   fingerprint, "earliest shared region" can be anything shared anywhere (an end-credits music bed at
   t≈1400 s), which C would anchor and snap. The opening-only assumption is load-bearing.
3. **It changes Introduction detection, not just recap.** Intro matching takes the *longest* shared
   region over its fingerprint window (`ChromaprintAnalyzer.cs:338-369`). Enlarge that window to the
   whole episode and the candidate set changes — shared end-credits/inter-episode music can win. B
   dismisses this as "the cost of a larger Intro fingerprint" (B §8), which **understates** it: it is a
   behavioural change to the most-used analyzer in the plugin.
4. **One-time re-decode storm + hash churn.** Promoting the Introduction range changes the
   detection-cache hash for *every* episode's Introduction fingerprint, invalidating the entire
   existing cache on upgrade. B's "+3.5 s/episode" cost (B §6.2) ignores that the *existing*
   opening-only intro cache is also invalidated.

If B instead takes option (a) (a distinct full-episode reference entry), there is **no collision with
C**, but then enabling **both** tier 3 (C, opening-only) and tier 4 (B, full-episode reference) means
**two** fingerprint decodes per episode — undercutting the "cheap" story exactly when the ensemble is
at full strength. D's tier table (D §2.5) lists B as "optional tier 4" and never picks which range owns
the cache. **This must be resolved before either C or B merges; it cannot be left to "optional."**

### 1.4 — A and C rewrite recap start in different places, with different definitions; the "shared reconciliation" is hand-waved *(High; attack #1)*

Both A and C fix the "start forced to 0" bug, but they encode **two different answers to "where does a
recap start,"** in two code paths that never meet:

- **A** anchors start to the **subtitle cue** that says "Previously on…": `start = anchorCue.Start`
  (`IntroSkipper/Subtitles/SubtitleRecapSegmentBuilder.cs:63-66`, branch `recap-rfc-a-subtitles`). End
  is the cue cluster, snapped to a black frame. A then proposes — but **does not build** — a
  recap-aware post-processor that applies "**end** adjustments … and leaves start untouched" (A §4.4,
  §5 pseudocode `adjustEndOnly`).
- **C** anchors start to the **audio sting**, or to a fade up to 6 s before it: `ResolveRecapStart`
  (`IntroSkipper/Analyzers/RecapDetectionHelper.cs:159-193`, branch `recap-rfc-c-harden`). End is the
  *earliest* valid black frame (`RecapDetectionHelper.cs:207-238`, same branch).

For the **same** cold-open-then-recap episode, A starts at the moment the voice-over says the phrase;
C starts at the fade preceding the audio sting. These differ by seconds and there is **no shared
definition** — exactly the inconsistency D flags as finding #4 ("boundary semantics differ by
signal"), and it **survives** adopting A+C because each ships its own start logic.

Worse, the *actual* residual corruptor is one neither D's reconciliation nor C's fix removes. I
verified C's recap segment still flows through the intro-shaped post-processor — `ChromaprintAnalyzer`
line 176 still calls `timeAdjustmentHelper.AdjustIntroTimesAsync(...)` **unchanged** on the C branch —
and `AdjustIntroBasedOnChapters` defaults **true** (`IntroSkipper/Configuration/PluginConfiguration.cs:316`).
`AdjustIntroTimesAsync` snaps any start `≤ EndSnapThreshold` to 0 **and**, when not snapping, pulls
the start to the nearest chapter boundary (`IntroSkipper/Analyzers/TimeAdjustmentHelper.cs:71-89`).
C's claim that a "40 s anchored start … survives `TimeAdjustmentHelper`" (C Bug 1) is true only for the
*snap*; the *chapter pull* can still move it. C admits this in its own additional finding #5 —
*"`AdjustIntroBasedOnChapters` (default on) can pull a cold-open-anchored recap start onto an unrelated
chapter mark. Left as-is"* — i.e. C **knowingly leaves a path that can re-corrupt the very start it
fixed.**

D's §2.3 reconciliation says "never blanket-snap to 0" and "refine end by black frame/silence," but
addresses only the two snap **sites** it enumerated (`ChapterAnalyzer.cs:272` and the `≤5⇒0` snap). It
never mentions `AdjustIntroBasedOnChapters`, the live residual corruptor after C's fix. So the
"deterministic shared reconciliation" is not coherent — it is three different, partly-unbuilt start
policies (A's `adjustEndOnly`, C's `ResolveRecapStart`, D's prose) with the real offender unmentioned.
**Resolution required: exactly one recap-shaped post-processing path (no chapter start-pull, end-only
structural snap), consumed by every tier — and it must be built, because none of A/C/the shipped path
implements it today.**

### 1.5 — C's earliest-region selection quietly undermines C's own cold-open fix *(High)*

C's marquee fix (non-zero, cold-open-aware start) is fed by the **earliest** shared region, which C did
**not** change: `SelectSharedRegion → GetEarliestTimeRange` picks the region with the smallest start
(`IntroSkipper/Analyzers/ChromaprintAnalyzer.cs:293-328`). On a real show with **any** earlier shared
audio than the recap sting — a network/distributor ident, a rating bumper, a recurring SFX at t≈2 s —
the "earliest shared region" is that ident, not the "Previously on" sting. Its start is
`≤ ColdOpenStartThreshold (5 s)` (`RecapDetectionHelper.cs:171-174`, branch `recap-rfc-c-harden`), so
`ResolveRecapStart` returns **0** and the cold-open branch never runs. C's own additional finding #3
concedes this exactly: *"If the earliest shared region is the theme/ident and a later region is the
real sting, the detector commits to the wrong one and there is no 'try the next region' path. The bug-3
guard rejects the theme but then yields **nothing** rather than the real recap."* So C's cold-open
handling only works when the recap sting is *itself* the earliest shared audio — frequently false for
the very cold-open-then-recap shape it targets.

### 1.6 — B's numbers are on data that doesn't resemble chromaprint; the "bug" is a non-defect *(High)*

B is the most rigorous-looking RFC and the most over-extrapolated.

**The benchmark input is uniform-random `uint[]` with byte-identical planted copies.**
`RandomFingerprint` fills each point with 4 random bytes (`IntroSkipper.Tests/TestCrossEpisodeReuse.cs:238-249`,
branch `b-cross-episode`); `Plant` is `Array.Copy` — an exact copy (`TestCrossEpisodeReuse.cs:251-252`).
For two independent random 32-bit values, the chance of a ≤6-bit Hamming match is ≈ 3×10⁻⁷, so noise
produces **zero** spurious matches and the planted clip stands out perfectly. That is why the benchmark
sees only **3–4 distinct shifts** (I reproduced: 3–4 shifts, 4.5–7.4 ms/pair). **Real chromaprint
points are the opposite of uniform-random** — consecutive points share most bits (a sliding spectral
hash) and common values (silence, sustained tones, music beds) recur thousands of times. B's own
honesty caveat admits the model "understates real chromaprint behaviour … real data will produce
**more** spurious votes and distinct shifts" (B §6.1). So the measured cost is a *floor* and the
measured *quality* (the true shift surviving the `TopShifts=16` cap) is the real, **unmeasured** risk.
Quoting "2.9–7.4 ms/pair" as the cost story without a real-data run is not supportable.

**The default pre-filter is set so low it never early-exits on real data.**
`PreFilterMinOverlap = 0.02` (`IntroSkipper/Analyzers/ReuseMatchOptions.cs:52-56`, branch
`b-cross-episode`) — only 2 % of the query's *distinct* point values must appear anywhere in a full
prior episode. Two episodes of the same show share intro music, silence, encoding artifacts, and tone;
distinct-value overlap will routinely exceed 2 %. So the cheap early-exit meant to protect "shows that
don't reuse footage" (B §5 step 1) will almost never fire, and B will run the full search on episodes
with no reuse. The MinHash that would make this `O(k)` is **not implemented** — `PointSetOverlap` is a
full `O(n)` set-intersection stand-in (`IntroSkipper/Analyzers/CrossEpisodeReuseMatcher.cs:48-62`,
same branch).

**The "concrete bug" is a non-defect in shipped behaviour.** B headlines *"I found a concrete bug …
`FindContiguous` computes its scan length as `min(lhs, rhs) − |shift|`, which goes negative … so the
comparison loop never runs"* (B §0/§4). I confirmed the demo test passes
(`TestCrossEpisodeReuse.cs:150-174`, branch `b-cross-episode`). But the shipped recap path fingerprints
the **opening only** for *both* episodes (`QueuedEpisode.cs:147-148`), so it can never construct the
large-shift, length-mismatched input that triggers the negative bound — the deep reuse it "misses" is
**structurally impossible to present** to the shipped code. So this is not a latent defect in current
behaviour; it is a limitation that only matters *if you adopt B's full-episode model.* B's §4 wording
("makes true reuse matching impossible with the shipped primitive") is accurate; the TL;DR's "I found a
concrete bug" oversells it as a shipped defect.

**Coverage on real shows is poor, and B says so.** Re-mixed recaps — clips under a music bed or
narration, which is *most* prestige-TV recaps — diverge beyond the 6-bit threshold and are
undetectable; B's own `FindReusedSpans_RejectsHeavilyAlteredAudio` proves it by design (B §9). B also
cannot distinguish a recap from a **reused-footage cold-open flashback** or a **recurring establishing
shot** — both are "reuse from the body" with montage-like clustering. The proposed discriminator ("add
a min-total-reused-duration gate," B §9) is **not built.** Season premieres need a cross-season queue
change (also not built). So B's "true model" claim holds only for the narrow "reused **original**
audio, no re-mix" case.

### 1.7 — A's "highest precision" is overstated *(Medium-High; attack #4)*

A is genuinely the cheapest signal and the cleanest fix for start-at-0, and its anchoring beats a naive
substring match. But "highest precision" is too strong:

- **Anchoring only rejects phrases that are *not* at the cue start.** `TryMatch` strips structured
  leading noise, then requires the phrase within `_anchorTolerance = 2` chars
  (`IntroSkipper/Subtitles/RecapPhraseMatcher.cs:107-132`, branch `recap-rfc-a-subtitles`). The
  falsified example A is proud of ("I told you previously on Tuesday…", phrase mid-cue) is rejected —
  but a **cold-open dialogue cue that *starts* with the phrase** is accepted. "Previously on the news,
  three were hurt." within the 150 s window → the builder anchors on the **first** matching cue with
  **no recap-context check** (`SubtitleRecapSegmentBuilder.cs:44-61`) and emits a recap over a normal
  scene. A admits the residual ("a show whose cold open literally opens with someone saying 'Last time
  on …'", A §7.6) but files it as low-risk; with a 150 s window and first-match-wins it is larger than
  billed.
- **A's own JP/KO defaults violate A's precision principle.** A argues English must be multi-word
  ("`previously on`, not bare `previously`", A §2.3) — then ships bare **`前回`** and **`これまでの`**
  for Japanese (`RecapPhraseMatcher.cs:77-79`, branch `recap-rfc-a-subtitles`). `前回` ("last
  time/previous time") is extremely common in ordinary Japanese dialogue ("前回会ったとき" = "when we
  last met"); `これまでの` ("up to now's…") likewise. These are the bare-"previously" over-match A
  explicitly avoided for English, so on JP content A's false-positive surface is materially worse than
  the English case its 58 tests validate. Same concern, milder, for `지난 이야기`.
- **CEA-608 split cues defeat single-cue anchoring.** Roll-up/pop-on captions frequently break the
  phrase across cues ("PREVIOUSLY" then "ON THE SHOW"). The matcher tests one cue at a time, so the
  phrase is never seen whole → recall miss. (ALLCAPS itself is fine — `Normalize` lower-cases,
  `RecapPhraseMatcher.cs:140-173`.)
- **Mistimed sidecars** shift start/end; A concedes the black-frame end-snap absorbs small drift but
  not gross desync (A §7.5).
- A's real-world recall is **explicitly "Assumed"** in its own verified-vs-assumed table (A §11). So
  "highest-precision, cheapest" is half-proven (cheap: yes; mechanism: yes; precision/recall on real
  libraries: unmeasured).

### 1.8 — AfterIntro is served by **no** Round-1 prototype; D's window for it is speculation *(High; attack #5)*

The seed dataset includes two `AfterIntro` labels (recap at `[95,128]`, `[102.5,140]`,
`seed-dataset.json` Neon Divide S1E2/E3). **Every** Round-1 approach scores zero on them:

- Shipped + C clamp the scan window to `[0, intro.Start]` (shipped
  `IntroSkipper/Analyzers/RecapDetectionHelper.cs:30-33`; C `RecapDetectionHelper.cs:52-61`, branch
  `recap-rfc-c-harden`). A recap *after* the intro is outside the window by construction. C lists this
  as ceiling #1 and rules it out of scope.
- **A forecloses the one path that could reach it.** A's builder window is `MaxWindowSeconds = 150`
  (no intro clamp), so a phrase cue at 95 s *could* be found — but A §6 then recommends the subtitle
  window "adopt the same ceiling" `min(duration, MaximumRecapDetectionDuration, intro.Start)` so the
  detectors agree. Adopting that ceiling drops the cue at 95 s when the intro starts at ~90 s. A
  neither tests nor claims AfterIntro.
- B's query is the opening window; an after-intro recap sits past it.
- D §2.3 rule 5 proposes searching `[intro.End, intro.End + MaximumRecapDetectionDuration]` "gated by a
  flag" — **purely a proposal; no prototype implements it.**

So the dataset contains a first-class shape (the project wiki lists "after the intro" as valid) that
the synthesis **cannot detect**, and the harness will faithfully report recall 0 for it forever. That
isn't a measurement win; it's a labeled shape with no detector and a roadmap item dressed as a tier.
Either build the after-intro window or stop listing AfterIntro as served.

### 1.9 — Real coverage is unquantified; where the whole thing still produces nothing or wrong *(High; attack #6)*

Stripping the optimism, realistic per-signal coverage on a typical user library:

- **Chapter markers (tier 1):** highest precision, but **rare** in user libraries unless the user runs
  SponsorBlock chapter import or the release embeds them. Most personal rips have none.
- **Subtitles that transcribe the recap (tier 2 / A):** gated by (a) a **text** sub existing — false
  for many anime and disc/PGS/VOBSUB rips; (b) that track **transcribing** the recap VO/card —
  forced-narrative tracks routinely omit it; (c) the language being in the phrase list. A is honest
  that 1–3 are "fundamental" (A §7). Net availability is content-dependent and **unmeasured**.
- **Shared sting + clean fade (tier 3 / C):** needs ≥2 episodes sharing near-identical opening audio
  *and* a black-frame boundary. Many modern shows have neither a "Previously on" sting nor fades
  between recap and cold open. C is explicit this is "a precision win and a correctness win, not a
  coverage win" (C §ceiling).
- **Footage reuse, original audio, no re-mix (tier 4 / B):** the narrow slice from §1.6.

Where the synthesis still emits **nothing**: unique-per-episode recaps with original VO, no shared
sting, no chapter, no transcribing text sub (a large modern-streaming slice). Where it emits **wrong**
output: any cold-open-then-recap where an earlier shared ident wins the earliest-region race (§1.5) →
start snapped to 0, skipping the cold open; or the black-frame-only fallback (if enabled) returning
`[0, earlyFade]` over a fade-bearing cold open with no recap at all
(`IntroSkipper/Analyzers/ChapterAnalyzer.cs:247-273`). **None of these rates is known**, because there
is no real corpus (§6).

---

## 2. Integration conflicts — how they MUST be resolved before any merge

1. **One recap fingerprint range owns the cache (resolves §1.3).** Decide explicitly: either (a) recap
   and intro keep the opening-only `(0, IntroFingerprintEnd)` shared entry (C as-is) and **B is
   deferred** / given its **own** `CacheEntryType` + range so it never aliases the Introduction key; or
   (b) if Introduction is ever promoted to full-episode, C's `GetFingerprintCacheMode(Recap)⇒
   Introduction` mapping must be **removed**, recap given its own opening-only entry, and the impact on
   **Introduction detection** (longest-region over a full episode) measured first. There is no
   configuration in which "C + B option (b)" is correct as written. Add the chosen range/flags to
   **both** the analysis hash and the detection-cache hash, and prove round-trip on the
   `e.Start == start && e.End == end` key.

2. **Exactly one recap post-processing path (resolves §1.4).** Build a recap-shaped finalizer:
   preserve the tier's start (no `AdjustIntroBasedOnChapters` start-pull, no `≤EndSnapThreshold⇒0` for
   recap), apply only an end-side structural snap (black frame/silence) within a small window. Every
   tier (A, C, future B) routes through it so start/end semantics are identical regardless of which
   signal fired. This is D's §2.3 intent but must exist in code and must include the
   `AdjustIntroBasedOnChapters` exemption D omitted.

3. **Fix earliest-region selection before trusting C's cold-open fix (resolves §1.5).** Either iterate
   shared regions (try-next when the earliest is guarded out) or gate the earliest region by a
   recap-likelihood check, so an early ident doesn't pre-empt the real sting.

4. **`AnalyzerAction.BlackFrame` for recap is a dead UI lever** — D finding #1, confirmed: the recap
   chain is only `[ChapterAnalyzer, ChromaprintAnalyzer]`
   (`IntroSkipper/ScheduledTasks/BaseItemAnalyzerTask.cs:361-365`) and the promotion case targets
   `BlackFrameAnalyzer or CreditsBlackFrameAnalyzer` (`BaseItemAnalyzerTask.cs:380-381`), never added
   for recap. Either wire a real recap black-frame analyzer or hide the lever; any ensemble that adds
   tiers must extend `AnalyzerAction` coherently, not leave more no-op levers.

---

## 3. Harness validity (attack #3, expanded)

- **Metric math is correct** (IoU/union/MAE are standard and unit-checked, `RecapMetrics.cs:52-61`).
  Credit where due: a media-free, deterministic harness is the right way to catch a PR #771-class
  silent regression (a detector that emits nothing scores recall 0). Keep it.
- **But the dataset cannot stress detectors (§1.1)** and **the metrics don't model harm (§1.2).**
- **The seed dataset is mildly rigged toward the easy shapes.** 9/14 with-recap entries are either
  `RecapFirst` (start = 0, which the *current* code gets right by accident) or `ColdOpenThenRecap` with
  clean single intervals; there are no adversarial entries (mid-episode "previously on" dialogue, an
  ident-before-recap, a fade-bearing NoRecap cold open, a long recap music bed, a 2-clip montage). A
  detector that simply emitted `[0, ~30]` for every episode would score *surprisingly well* on the
  `RecapFirst` rows and lose only the cold-open/after-intro rows — flattering the start-at-0 behaviour
  the research is trying to kill.
- **"First-valid-wins" (Level 0) cannot let a better later tier override a worse earlier one.** D says
  so — Level 0 "cannot … let a *later, higher-confidence* signal override an *earlier, lower-confidence*
  one" (D §2.2). For the proposed order (Chapter→Subtitle→Sting→Reuse) that is *mostly* fine because
  tiers are precision-ordered — **but** the black-frame fallback lives **inside tier-1
  ChapterAnalyzer** and can mark an episode `Analyzed` before the higher-precision subtitle/sting tiers
  run (D finding #2, confirmed — `DetectRecapUsingBlackFramesAsync` is a fallback inside
  `ChapterAnalyzer`, `ChapterAnalyzer.cs:111-114`). So at Level 0 a **low-precision structural guess
  can pre-empt a high-precision signal**, which is backwards. Either demote black-frame out of tier 1
  in code (D §2.1 says so; the synthesis must actually do it) or accept that "first-valid-wins" is
  wrong here and pay for Level 1 confidence. The synthesis can't have it both ways.

---

## 4. Precision stress tests I constructed

- **A, mid-episode opener:** cue at t=40 s "Previously on the morning news…" (in-scene TV) → matches,
  first-wins, recap emitted over a normal scene. **FP.** (Anchoring doesn't help; phrase is at cue
  start.)
- **A, Japanese dialogue:** cue at t=12 s "前回はありがとう" ("thanks for last time") → bare `前回`
  matches → **FP.** Bare-word JP defaults (§1.7).
- **A, split CEA-608:** "PREVIOUSLY" / "ON THE ISLAND" across two cues → no single-cue match →
  **miss.**
- **C, false-positive guard misfire (both directions):** the guard rejects shared regions >
  `StingMaximumDuration = 20 s` when no intro is detected (`RecapDetectionHelper.cs:41,122-125`, branch
  `recap-rfc-c-harden`). A real show with a **long shared recap music bed (> 20 s)** and intro
  detection disabled/first-episode → the genuine recap is **rejected** (C concedes, ceiling #5).
  Conversely a **short (< 20 s) recurring studio ident + a fade**, no intro detected, **passes** the
  guard and is emitted as a recap (C concedes, ceiling #6). So the 20 s line both over- and under-
  fires; it's a heuristic, not a fix — and C says so, but the synthesis treats "false-positive guard"
  as solved.
- **C, short-theme show:** a 12 s opening theme recurring across episodes, no intro detected, with a
  fade after it → satisfies "short shared region + montage boundary" → emitted as recap. **FP.**

---

## 5. The visual recap-card signal (PR #798 entropy/saturation) — ranked

PR #798 (`53676e9` "Detect non-black 'card' credits via an entropy/saturation fallback", branch
`capy/credits-nonblack`) decodes keyframes and emits, per keyframe, normalized luma-histogram
**entropy** and mean **saturation** (`-vf entropy,signalstats,metadata=print`, parsed into
`KeyframeVisual`), then flags a "card" frame as `Entropy < 0.35 && Saturation < 96`
(`IntroSkipper/Analyzers/Credits/CreditEntropyFallback.cs:56-58`; thresholds
`CreditDetectionPolicy.cs:35-41`) and looks for a **sustained** run of them. Assessed as a **recap**
cue:

- **As a recap-content detector: structurally incapable — CONFIRMED.** The signal fires on a
  *sustained low-entropy uniform* frame; a recap is a **montage of high-entropy reused footage** —
  busy, textured frames identical in entropy to normal episode content. `IsCreditCardKeyframe` returns
  false for every montage frame. PR #798's own boundary makes this explicit: credits **over moving
  video stay undetected**, and recap footage *is* moving video. Entropy cannot pick recap content out
  of episode footage. There is no version of "low-entropy uniformity" that segments a montage.
- **Where it *might* work — the burned-in "Previously on…" title card.** A title card (text on
  solid/dark/colored background) is low-entropy/low-saturation — the **visual twin** of A's subtitle
  phrase cue, and it catches the one case A misses: a burned-in card with no subtitle/forced track. But
  two problems gut the precision: (1) entropy says "**a** card is here," not "the **recap** card." The
  episode head is full of low-entropy cards — main title, location/time cards ("3 days earlier"),
  content-rating bumpers, distributor/studio idents, sponsor cards. Disambiguating needs **OCR** (read
  the card) or strong **position/timing** priors, neither of which this plumbing has. (2) PR #798's
  mechanism requires a **sustained run ≥ minimumDuration** with cadence merging and tail-trimming
  (`CreditEntropyFallback.cs:21-49`); a real "Previously on" card is on screen ~1–3 s, so it would
  likely **fail the sustained-run test** — the design is tuned for a multi-minute credit roll, not a
  brief head card. Even the favorable case fights the mechanism.
- **False-positive surface vs #798: multiples worse.** #798 is safe **only** because it is (a)
  **tail-anchored** — it decodes from `CreditsFingerprintStart`, the last ~25 % of the file, where the
  *only* expected uniform-card content is the credits; (b) a **fallback** that runs when black-frame
  finds nothing; (c) selects the **latest** run (credits are last). The episode **head** is the
  worst-possible region: card-**dense** (idents + rating + title + location/time + sponsor cards —
  typically several distinct low-entropy events in the first 2–3 minutes vs ~one card cluster in the
  credits tail), with **no** "it's the last thing" structure to lean on and a "latest run" tiebreaker
  that is meaningless at the head. So the FP risk is several times higher than #798's niche, and #798's
  safety argument does not transfer.
- **Adjacent variant — cut-rate / entropy-DELTA density — is more on-target but still loses.** A
  montage cuts fast, so per-keyframe *change* (entropy delta / histogram distance) is high and
  sustained over the montage — conceptually more recap-specific than absolute uniformity, and it reuses
  the exact same `DetectKeyframeVisualsAsync` plumbing. But: (1) keyframes (`-skip_frame nokey`) are
  GOP-spaced — often 2–10 s apart, far longer for long-GOP — much **too coarse** to measure 1–3 s
  montage cuts; you'd need scene-change/full-frame decode (`select='gt(scene,…)'`), which is **heavier**
  and pushes against the "not heavy" constraint that started this effort (issue #136). (2) Fast cutting
  is **not recap-specific** — action beats, title sequences, and many cold opens cut fast too. (3) It
  yields "something montage-like is here," not a boundary or a "this is a recap" semantic. Worth one
  experiment on the existing plumbing; **not** worth a tier.
- **Cost is its only virtue, and cost was never the bottleneck.** The signal is cheap — the head
  black-frame scan the recap path already runs could emit entropy/saturation in the same decode by
  appending `entropy,signalstats,metadata=print`. But precision, not cost, is what fails recap
  detection, so a cheap-but-imprecise card detector doesn't move the needle.

**Rank: weakest of all recap signals — strictly dominated.** Below Chapter, Subtitle (A), and
Sting+blackframe (C); roughly level with or below Cross-episode reuse (B) depending on library. Its
only unique value (a burned-in card with no subtitle) overlaps heavily with what A already recovers via
forced-subtitle tracks. **Recommendation: not a tier.** At most a last-resort corroborator, and only if
paired with OCR to turn "a card is here" into "the recap card is here." Without OCR it adds false
positives in the card-dense head for no precision gain.

---

## 6. Minimum real-media validation protocol (required before anything defaults on or B is wired in)

The harness is necessary but not sufficient. Before any tier beyond Chapter defaults on — and before B
touches the fingerprint cache — there must be a **real labeled corpus** and **harm-aware metrics**.

**Corpus (real episodes, not synthetic):**
- **Shapes × genres grid.** Shapes = {RecapFirst, ColdOpenThenRecap, AfterIntro, NoRecap}; genres =
  {live-action serialized drama, procedural, sitcom, anime, reality/doc}. **≥ 30 episodes per
  (shape × genre) cell** that exists — a few hundred labeled episodes minimum. D's "~30–50 per shape
  per genre" (D §5) is the right order; make it a floor.
- **NoRecap must be the majority (≥ 50 % of the corpus)** so the false-positive rate is statistically
  meaningful (FP denominator = NoRecap count, `RecapMetricsSummary.cs`, branch
  `capy/recap-rfc-ensemble-eval`).
- **Languages ≥ 3**, including **at least two non-Latin scripts (JA + KO)** to exercise A's bare-word
  phrases and the image-sub/CEA-608 fallthrough, plus at least one show with **image-only** subs and
  one with a **burned-in** card and no sub.
- **Multiple contributors' libraries** to avoid one collection biasing the result.

**Labels must include per-signal availability, not just the interval** (else you can't attribute recall
to a tier or compute coverage): `hasRecap, recapStart, recapEnd, sourceShape`, **plus**
`hasChapterMarker`, `hasTextSubTranscribingRecap`, `hasSharedSting`, `footageReuse ∈
{none,original-audio,remixed}`, `hasBurnedInCard`. The harness schema must be extended to carry these.

**Metrics to add (fixing §1.2):**
- **Content-skip seconds** = seconds of **non-recap** wrongly inside the detected interval (cold open
  or episode body), reported separately and weighted **heavier** than missed-recap seconds. This is
  the number that captures "wrongly skipped story."
- **Split the FN bucket** into *silent miss* (safe) vs *fired-but-wrong* (harmful); never report them
  as one number.
- **FP rate on NoRecap** reported and **gated** independently of IoU.
- Keep IoU/MAE but demote them from gates to diagnostics; for a skip button, **start over-reach and
  content-skip dominate**.

**Acceptance thresholds (proposed; tune on data):**
- On the NoRecap majority: **FP ≤ 2 %** per enabled tier (a story-skip on a no-recap episode is the
  cardinal sin).
- On with-recap: **zero** detections that skip **> 3 s** of pre-recap content; median end-error
  **≤ 2 s**.
- Each tier's **marginal recall** (ablation: chapters → +subtitle → +sting → +reuse) must exceed its
  **marginal FP cost**; a tier that fails this **defaults off**.
- Run on **≥ 2 ffmpeg builds** (system 6.1.1 + jellyfin-ffmpeg). The `TestSilenceDetection` flake I
  reproduced proves boundary numbers move across builds, so any black-frame/entropy threshold tuned on
  one build must be checked on another.

**Process:** fix one `labels.json`; each branch exports `detections.json` via D's §3.7 adapter on the
same real episodes; diff per-shape and per-availability rows; choose defaults from the ablation. Until
this exists, **ship C-only (with §1.5/§1.4 fixed) + A behind a default-off flag + D's harness as a
regression guard** — and treat all accuracy language as hypothesis.

---

## 7. Independent ranking (precision × coverage × cost × risk) and verdict on the synthesis

| Approach | Precision (when it fires) | Coverage | Cost | Integration risk | Merge-readiness |
|---|---|---|---|---|---|
| **C — harden** | M→H (fixes real boundary bugs) | **unchanged/narrower** (own admission) | ~free (shared fingerprint) | **Low–Med** (touches shipped path; §1.5/§1.4 must be fixed) | **First.** Only thing that improves the currently-shipping, just-unbroken detector. |
| **A — subtitles** | H *when a text sub transcribes the recap*; FP holes (§1.7) | content-dependent, unproven | **Lowest** (no A/V decode) | Low–Med (needs the unbuilt recap-shaped finalizer; JP/KO phrase de-risk) | **Second, behind a default-off flag.** Best *new* signal; build start-preservation before it defaults on. |
| **D — ensemble+eval** | n/a (plumbing) | n/a | trivial | Low | **Adopt harness + tier *naming* now; do NOT treat green metrics as validation.** Extend schema/metrics per §6. |
| **B — cross-episode reuse** | H only for *original-audio, non-remixed* reuse | **narrow** (re-mix kills it; flashbacks/idents confuse it) | **Highest** (full-episode decode) | **High** (§1.3 cache conflict; queue change; Intro-detection side effects) | **Defer.** Most research-y, least merge-ready; do not wire into the cache until §1.3 is resolved and real-data quality shown. |
| **PR #798 visual entropy** | L (card ≠ recap card; needs OCR) | L (burned-in card only) | Low | Med (card-dense head FP) | **Not a tier.** Last-resort corroborator only with OCR. |

**Do I agree with Captain's tiered synthesis (Chapter → Subtitle(A) → hardened sting+blackframe(C) →
optional cross-episode(B), measured by D)?**

**Agree with the shape; reject the readiness and three specifics.** Ordering by precision-then-cost is
sound, and naming the tiers improves on the implicit chain. But as stated the synthesis is over-sold
and internally under-specified:

1. **Downgrade "measured by D's harness" to "regression-guarded by D's harness; accuracy gated by §6's
   real-media protocol."** The harness measures arithmetic on intervals, not detector accuracy (§1.1),
   and its metrics don't model user harm (§1.2). As written, "measured by D" is the load-bearing claim
   and it doesn't bear the load.
2. **B is not "optional tier 4"; it is "deferred."** Bolting B on as recommended (full-episode
   Introduction) silently breaks C's cache dedup and perturbs Introduction detection (§1.3). It must
   not enter the cache path until its range ownership is resolved and its real-data quality (not
   synthetic) is shown. "Optional" invites someone to flip it on and quietly regress intros.
3. **The "shared reconciliation" must be built, not assumed.** A and C ship two different start
   definitions, and the real residual corruptor (`AdjustIntroBasedOnChapters` start-pull) is
   unaddressed by all of A/C/D (§1.4). One recap-shaped finalizer, consumed by every tier, is a
   prerequisite — not a later nicety.
4. **Drop AfterIntro from "served shapes" until a detector exists** (§1.8), and **demote the
   black-frame fallback out of tier-1 in code** so it stops pre-empting higher-precision tiers at
   Level 0 (§3).
5. **The visual entropy signal is not a tier** (§5).

Net: the evidence-supported near-term plan is **C (fixed) + A (flagged off) + D's harness as a guard**,
with a real corpus built before anything else defaults on and before B is wired in. That is a smaller,
more honest step than "ship the four-tier ensemble measured by the harness," and it is the only version
the evidence available today supports.
