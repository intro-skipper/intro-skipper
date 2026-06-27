# RFC B — Recap detection via cross-episode content-reuse matching

> **Status:** research + design spike (NOT a mergeable feature). Prototype + tests included; analyzer is **not** wired into the chain.
> **Author:** spike investigation on branch `recap-rfc-b-cross-episode-reuse`.
> **Scope:** design and de-risk recap detection by matching the opening of episode *N* against **prior** episodes to find reused footage/audio, and critically review the current recap implementation from that lens.
> **Environment for all measurements:** Ubuntu 24.04 (x86-64), .NET 10.0.102 SDK building `net9.0`, system `ffmpeg 6.1.1`, **Debug** build, single thread. Numbers are conservative (Debug, not Release).

---

## 0. TL;DR verdict

* **A recap is reused footage/audio from earlier episodes.** The defining property is *cross-episode reuse*: the recap’s audio does not match episode *N*’s new content but **does** match spans somewhere inside prior episodes. Intro detection is *opening-vs-opening at the same offset*; recap detection is *opening-vs-**anywhere-in-prior***.
* **The shipped recap detector cannot do this.** It is the **degenerate special case** of reuse matching: it fingerprints only `(0, IntroFingerprintEnd)` of *both* episodes [[QueuedEpisode.cs:148]](../../IntroSkipper/Data/QueuedEpisode.cs#L148) and finds the *earliest shared* opening region [[ChromaprintAnalyzer.cs:293-328]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L293-L328). That only ever finds a recurring “Previously on…” **bumper sting**, never the reused **clips** (which live deep in the prior episode and are never fingerprinted). The recap’s *extent* is then guessed from black frames, not audio.
* **I found a concrete bug** that makes true reuse matching impossible with the shipped primitive: `FindContiguous` computes its scan length as `min(lhs, rhs) - |shift|` [[ChromaprintAnalyzer.cs:453]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L453), which goes **negative** for the large shifts a deep reuse implies, so the comparison loop never runs. A unit test demonstrates production missing a deep reuse the spike finds.
* **The audio approach is cheap enough.** Measured on this VM: the per-pair comparison is **2.9–7.4 ms** even against a 60-min reference; the real cost is obtaining the prior episode’s **full** fingerprint, which is **one cached ~4.6 s ffmpeg audio decode per episode (~310× realtime, zero GPU)** — the same order as the per-episode fingerprinting the plugin already performs for Introduction/Credits, and far below Prime Video’s per-shot **video** alignment.
* **Honest limits.** Reuse matching cannot detect recaps that are **re-mixed** (a music bed / narration laid over the clips changes the audio spectrum and defeats fingerprinting), shows that **don’t reuse footage** (newly-shot “story so far”), or **season-premiere** recaps of the previous season unless prior selection crosses the season boundary. The shift-discovery phase is also more fragile to re-encoding than the extraction phase (see §7.4 / §9).

**Recommendation:** worth prototyping into an opt-in analyzer **for the audio path only**, scoped to *N* vs *N-1* (and optionally *N-2*), with a MinHash/point-set pre-filter and a bounded top-T shift search. Do **not** pursue video corroboration under the “not heavy” constraint. Keep the existing black-frame boundary snap as an optional refinement, not the primary signal.

---

## 1. What a recap is, and why reuse matching is the right model

A recap (“Previously on…”) is a montage near the start of an episode assembled from **clips taken from earlier episodes**. Concretely:

* The recap’s **audio/video is literally reused** from prior episodes; it differs from episode *N*’s new content.
* It is short (≈10–60 s), often a **montage of several disjoint clips** stitched back-to-back, sometimes bounded by black-frame/fade transitions.
* Placement varies: before or after the cold open, before or after the intro.
* It surfaces in Jellyfin as `MediaSegmentType.Recap` [[SegmentProvider.cs:28]](../../IntroSkipper/Providers/SegmentProvider.cs#L28).

The reuse property gives a precise detector: **take episode *N*’s opening window and find which chunks of it are reused from prior episode(s)** — those reused chunks *are* the recap.

| | **Intro** (shipped) | **Recap** (this RFC) |
|---|---|---|
| Compare | opening of *A* ↔ opening of *B* | opening of *N* ↔ **full** prior episode(s) |
| Alignment | shared region at ~**same offset** in both (small shift) | reused clips at **arbitrary offsets** in the prior (large, varied shifts) |
| Result shape | one contiguous shared region | **several disjoint** reused spans, contiguous in *N* (a montage) |
| Reference window | opening only (`0..IntroFingerprintEnd`) | **whole** prior episode |

---

## 2. Fingerprint mechanics (the units everything is measured in)

Chromaprint emits one 32-bit point per hop. The hop duration is the single source of truth for all time↔point conversions:

```
SampleDuration = 4096 / 11025 / 3 ≈ 0.1238397 s/point   // ChromaprintConstants.cs:22
points/second  = 1 / SampleDuration ≈ 8.075
```
[[ChromaprintConstants.cs:22]](../../IntroSkipper/Data/ChromaprintConstants.cs#L22)

Measured point counts (full episode, via `ffmpeg -f chromaprint`), matching the model:

| Episode length | Seconds | Points (predicted 8.075/s) | Points (measured) |
|---|---|---|---|
| 22 min | 1320 | 10,659 | — |
| **24 min** | **1440** | **11,628** | **11,614** ✅ |
| 42 min | 2520 | 20,349 | — |
| 60 min | 3600 | 29,070 | — |

The **current recap fingerprint window** is only the opening: `Recap => (0, IntroFingerprintEnd)` [[QueuedEpisode.cs:148]](../../IntroSkipper/Data/QueuedEpisode.cs#L148), and `IntroFingerprintEnd` is computed in the queue as [[QueueManager.cs:245-247]](../../IntroSkipper/Manager/QueueManager.cs#L245-L247):

```
IntroFingerprintEnd = min( duration≥300 ? duration·AnalysisPercent : duration,
                           60 · AnalysisLengthLimit )
defaults: AnalysisPercent = 25 %, AnalysisLengthLimit = 10  ⇒ cap = 600 s
  24-min ep ⇒ min(360, 600) = 360 s ≈ 2,907 points
  42-min ep ⇒ min(630, 600) = 600 s ≈ 4,845 points
```

**Key consequence:** today, recap mode only ever has the **first 360–600 s** of each episode fingerprinted. The reused clips of a montage come from the *body* of prior episodes (e.g. t≈900 s), which are **never fingerprinted** — so true reuse matching is structurally impossible with the current fingerprint range. This is the architectural gap RFC B closes.

---

## 3. Exact map of the current recap implementation

Recap is wired as: ChapterAnalyzer (regex / SponsorBlock / optional black-frame fallback) → ChromaprintAnalyzer (audio).

1. **Mode list & chain.** Recap is enabled by `ScanRecap` [[BaseItemAnalyzerTask.cs:77]](../../IntroSkipper/ScheduledTasks/BaseItemAnalyzerTask.cs#L77); the Chromaprint analyzer is appended for non-movie items with valid ffmpeg [[BaseItemAnalyzerTask.cs:361-365]](../../IntroSkipper/ScheduledTasks/BaseItemAnalyzerTask.cs#L361-L365).

2. **Fingerprint range.** `FingerprintAsync` reads `GetFingerprintRange(Recap) = (0, IntroFingerprintEnd)` [[QueuedEpisode.cs:143-152]](../../IntroSkipper/Data/QueuedEpisode.cs#L143-L152), [[FFmpegService.cs:118-124]](../../IntroSkipper/FFmpeg/FFmpegService.cs#L118-L124). This case **was missing until commit `e17f044` “fix recap”** (Jun 27 2026); before it, recap fingerprinting threw `ArgumentException` and aborted analysis — strong evidence the recap path was never validated end-to-end.

3. **Pairwise comparison.** `AnalyzeMediaFiles` fingerprints every episode in the season, then runs an **all-pairs** loop (pop one, compare against all remaining) [[ChromaprintAnalyzer.cs:90-180]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L90-L180). For each pair `CompareEpisodes` → `SearchInvertedIndex` → `FindContiguous` [[ChromaprintAnalyzer.cs:193-213]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L193-L213).

4. **Inverted index.** `CreateInvertedIndex` maps each point value to the **last** index it appears at — `invIndex[point] = i` overwrites earlier occurrences [[ChromaprintAnalyzer.cs:498-519]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L498-L519).

5. **Shift discovery.** For each point in LHS, probe RHS at `±InvertedIndexShift` (default ±2) **in value space**; each hit yields one candidate shift `rhsLast - lhsLast` [[ChromaprintAnalyzer.cs:379-424]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L379-L424).

6. **Contiguous match.** For each shift, `FindContiguous` XORs the two arrays and keeps points whose popcount ≤ `MaximumFingerprintPointDifferences` (default 6) [[ChromaprintAnalyzer.cs:432-490]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L432-L490).

7. **Recap selection.** In recap mode, `SelectSharedRegion` picks the **earliest** shared region (≥ `RecapCardMinimumDuration = 3 s`) [[ChromaprintAnalyzer.cs:26]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L26), [[ChromaprintAnalyzer.cs:234-242]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L234-L242), [[ChromaprintAnalyzer.cs:293-328]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L293-L328), and **snaps the start to 0** when it begins within 5 s [[ChromaprintAnalyzer.cs:317-325]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L317-L325).

8. **Boundary from black frames.** `BuildRecapFromChromaprintCandidateAsync` then ignores the audio region’s *end* and re-derives the recap end as the **latest black frame** before the intro [[ChromaprintAnalyzer.cs:255-291]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L255-L291) → `ChapterAnalyzer.BuildRecapFromBlackFrames` [[ChapterAnalyzer.cs:247-273]](../../IntroSkipper/Analyzers/ChapterAnalyzer.cs#L247-L273), bounded by `RecapDetectionHelper.GetMaximumBoundaryAsync` (min of `MaximumRecapDetectionDuration` and the detected intro start) [[RecapDetectionHelper.cs:21-36]](../../IntroSkipper/Analyzers/RecapDetectionHelper.cs#L21-L36).

9. **Cache & config hashing.** Fingerprints are Brotli-cached in SQLite keyed by `(ItemId, Mode, Type, Start, End)` + a config hash [[DetectionCacheService.cs:30-73]](../../IntroSkipper/FFmpeg/DetectionCacheService.cs#L30-L73), [[DbDetectionCache.cs]](../../IntroSkipper/Db/DbDetectionCache.cs). The cache key’s `Start/End` come from `GetFingerprintRange` and are compared with `==`, so they must round-trip bit-exactly. Analysis output is hashed per mode incl. the Recap case [[ConfigHasher.cs:44-49]](../../IntroSkipper/Helper/ConfigHasher.cs#L44-L49); the Chromaprint cache hash is mode-scoped [[ConfigHasher.cs:78-79]](../../IntroSkipper/Helper/ConfigHasher.cs#L78-L79).

---

## 4. The bug: `FindContiguous` cannot reach a deep reuse

`FindContiguous` is built for two arrays of **comparable length, aligned near the front** (intros). Its scan length is [[ChromaprintAnalyzer.cs:453]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L453):

```csharp
var upperLimit = Math.Min(lhs.Length, rhs.Length) - Math.Abs(shiftAmount);
for (var i = 0; i < upperLimit; i++) { ... }   // ChromaprintAnalyzer.cs:456
```

A recap reuses footage from **deep** inside a prior episode. If the reused clip is at reference index ≈ 8000 but query index ≈ 100, the discovered shift is ≈ **7900**. With a short opening query (`min(len)` ≈ 970) the scan length is `970 - 7900 < 0`, so **the loop never executes** and the reuse is invisible. The shipped engine can therefore only match reuse at **small** shifts (intro-like alignment), never the large shifts that define a recap.

This is proven by the test `Production_FindsAlignedReuse_ButMissesDeepReuse_WhereSpikeSucceeds` [[TestCrossEpisodeReuse.cs]](../../IntroSkipper.Tests/TestCrossEpisodeReuse.cs): the **same** planted block is found by `ChromaprintAnalyzer.CompareEpisodes` at a small offset (CASE A, `Valid == true`) but **not** at a deep offset (CASE B, `Valid == false`), while the spike recovers CASE B.

The fix is structural, not a one-liner tweak: derive the valid scan range from the **actual index bounds of both arrays** so any shift works. See `CrossEpisodeReuseMatcher.ExtractContiguousRunAtShift` [[CrossEpisodeReuseMatcher.cs:183-250]](../../IntroSkipper/Analyzers/CrossEpisodeReuseMatcher.cs#L183-L250):

```
qStart = max(0, -shift)
qEnd   = min(query.Length, reference.Length - shift)   // overlap given an arbitrary shift
```

---

## 5. Algorithm (audio path)

Spike implementation: `IntroSkipper/Analyzers/CrossEpisodeReuseMatcher.cs` (+ `ReuseMatchOptions`, `ReusedSpan`, `ReuseMatchDiagnostics`). Defaults are translated from the shipped Chromaprint tuning so behaviour maps onto production.

**Inputs**
* `query` = episode *N*’s opening window, e.g. first **120 s** (≈ 969 points). (Today’s `(0, IntroFingerprintEnd)` = 360–600 s also works but is larger than necessary.)
* `reference` = a prior episode’s **full** fingerprint (≈ 11.6k points for 24 min).

**Pseudocode**

```
FindReusedSpans(query, reference, opts):
    # (1) Cheap pre-filter / early-exit — skip shows that don't reuse footage
    overlap = |distinct(query) ∩ distinct(reference)| / |distinct(query)|     # MinHash on real data
    if overlap < opts.PreFilterMinOverlap: return [] , earlyExit=true

    # (2) Multimap inverted index of the reference (EVERY occurrence, not just the last)
    refIndex : point -> [all indices]          # vs production's last-occurrence-only index

    # (3) Shift voting (a 1-D Hough transform over offsets)
    votes : shift -> count
    for q in query:
        for delta in [-IndexShift .. +IndexShift]:           # value-space jitter, like production
            for refIdx in refIndex[query[q] + delta] (capped at MaxVotesPerPoint):
                votes[refIdx - q] += 1
    #   a reused clip of length L produces ~L votes at ONE shift; noise scatters ~1-2 across many

    # (4) Bounded extraction — only the strongest TopShifts are fully scanned (hard cost cap)
    spans = []
    for shift in top-T(votes, opts.TopShifts):
        span = ExtractContiguousRunAtShift(query, reference, shift, opts)   # corrected bounds (§4)
        if span: spans.append(span)
    return dedupeByQueryOverlap(spans)

AssembleRecap(spans, opts):
    # (5) A montage = several disjoint reused spans that are CONTIGUOUS in episode N.
    cluster spans whose query-space gap ≤ MaxMontageGapPoints   # absorbs cuts / short fades
    return hull(earliest qualifying cluster) in seconds         # start NOT forced to 0
```

**Why two phases matter.** Discovery (step 3) needs only *near-exact* value matches to *locate* a shift; extraction (step 4) then tolerates up to 6 differing bits to measure the clip’s full extent. A genuine clip of length *L* yields a vote peak of ≈ *L* at its true shift, trivially separable from the ≈1–2 votes random coincidences scatter (validated in the benchmark: only **3–4 distinct shifts** arise from a 3-clip montage in noise).

**Robustness guard.** Extraction requires `MinRunPoints` *genuine* matches, not merely a wide first-to-last span, so two coincidental matches that happen to fall within the gap tolerance cannot fake a run [[CrossEpisodeReuseMatcher.cs:241-247]](../../IntroSkipper/Analyzers/CrossEpisodeReuseMatcher.cs#L241-L247).

**Boundary construction.** The recap is the **hull of the earliest contiguous cluster**, converted to seconds. Start is **not** forced to 0, so a cold open before “Previously on…” is correctly excluded — a direct improvement over `GetEarliestTimeRange`’s `Start ≤ 5 ⇒ 0` snap [[ChromaprintAnalyzer.cs:317-325]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L317-L325). The shipped black-frame snap can still be applied as an optional *refinement* to land on a clean cut.

---

## 6. Cost & memory analysis vs the “not CPU/GPU heavy” constraint

The maintainer constraint (issue #136) is *“find a way that’s not CPU/GPU heavy.”* Two costs, analyzed separately.

### 6.1 Comparison CPU (runs every analysis, even on cache hits)

Per episode pair, dominated by: build reference multimap `O(n)`; pre-filter set ops `O(n+m)`; voting `O(m·(2·shift+1))` lookups; extraction `TopShifts · O(m)`. **`TopShifts` (default 16) hard-caps the expensive phase regardless of input.**

Measured by `Benchmark_RealisticSizes_IsCheap` (Debug, single thread, 200 iterations, 3-clip montage planted in random noise):

| prior-episode size | query window | distinct shifts | shifts scanned | spans | **avg ms / pair** |
|---|---|---|---|---|---|
| 22 min (10,659 pts) | 970 pts | 4 | 4 | 3 | **2.86** |
| 24 min (11,628 pts) | 970 pts | 3 | 3 | 3 | **4.82** |
| 42 min (20,349 pts) | 970 pts | 3 | 3 | 3 | **4.74** |
| 60 min (29,070 pts) | 970 pts | 3 | 3 | 3 | **7.45** |

Growth tracks reference size (the `O(n)` index build + allocations dominate), **not** the search. For a 12-episode season compared *N* vs *N-1* (11 pairs): **≈ 40–80 ms of pure comparison CPU for the entire season**, in Debug. Release would be materially faster. This is negligible. **Zero GPU.**

> **Honesty caveat:** the benchmark uses *uniform-random* `uint[]`, which understates real chromaprint behaviour — real points are temporally correlated and skewed, so real data will produce **more** spurious votes and distinct shifts than the 3–4 seen here. That only inflates the voting tally and the *candidate* shift count; the `TopShifts` cap keeps the extraction cost fixed, so the worst case stays bounded. The risk it introduces is *quality* (a real shift pushed out of the top-T by spurious peaks), not *cost*.

### 6.2 Decode CPU (one-time per episode, cached)

True reuse matching needs the prior episode’s **full** fingerprint, which the current pipeline never produces for recap. Measured full-episode chromaprint decode (24-min / 1440 s audio, median of 3):

```
~4.6 s wall/CPU per 24-min episode  ⇒  ~310× realtime, single core, no GPU
```

Context:
* Today’s recap decodes only `(0, 360 s)` ≈ **1.1 s**; the reference upgrade adds ≈ **3.5 s/episode** (24-min) — **one-time and cached** (Brotli SQLite). Re-runs and settled-season re-analysis reuse it.
* It is the **same order** as the per-episode fingerprinting the plugin already performs for Introduction (`0..360 s`) and Credits (tail ≈ 360–450 s) — combined those already decode ≈ half the episode under *different* cache keys.
* It is **dramatically** below Prime Video’s published recap/intro/credits approach, which aligns recap **shots to prior-episode video** (decode + downscale + per-frame hashing + alignment over image-scale data).

### 6.3 Memory

| structure | size (24 min / 60 min) | notes |
|---|---|---|
| full fingerprint `uint[]` | 46 KB / 116 KB | one per episode |
| multimap `Dictionary<uint,List<int>>` | ≈ 0.8 MB / 2.1 MB | heaviest; per-entry `List<int>` allocs |
| pre-filter `HashSet<uint>` | ≈ 0.2 MB / 0.5 MB | transient |

A 12-episode season held in memory (fingerprints + on-demand indexes) is **single-digit MB**, on par with the existing `fingerprintCache`/`_invertedIndexCache` the analyzer already keeps for a season. The multimap is the obvious optimization target (replace with a sorted `(value,index)` array + binary search to remove per-point list allocations) if memory ever matters.

### 6.4 Worst case vs typical

* **Typical** (*N* vs *N-1*, 120 s window, MinHash prefilter): ≈ 5 ms + amortized index per pair; one cached 4.6 s decode/episode.
* **Worst case** *if naively copied from the shipped all-pairs loop* [[ChromaprintAnalyzer.cs:90-180]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L90-L180): `O(E²)` pairs each against a full-episode reference. For E=24 that’s ~276 pairs × ~5 ms ≈ 1.4 s comparison — still cheap, but pointless. **Recommendation: recap reuse matching must be sequential (*N* vs *N-1*, optionally *N-2*), not all-pairs.**

**Verdict on the bar:** the **audio** path meets “not CPU/GPU heavy.” The comparison is trivial; the only real cost is a cached, one-time, single-core, ~310×-realtime full-episode audio decode — strictly more than today’s opening-only recap decode, but the same order as existing per-episode fingerprinting and orders of magnitude below any video approach.

---

## 7. Cheap-enough variant (minimal viable subset)

If even one extra full decode per episode is unacceptable, the cheapest viable subset that still detects the common case:

1. **Window the query to ≤ 90–120 s** of *N* (recaps lead the episode) → ≈ 727–969 query points.
2. **Compare only against *N-1*.** Most recaps draw heavily from the immediately preceding episode.
3. **MinHash (k≈64–128) pre-filter** on the prior episode → `O(k)` early-exit for shows that don’t reuse footage, before any full search or even full index build.
4. **Bounded top-T (≤16) shift extraction** with early-exit once a cluster covering ≥ `MinimumRecapDetectionDuration` is assembled.
5. **Reuse cached fingerprints** and, ideally, make the *full-episode* fingerprint serve both Introduction and Recap so the marginal decode is shared (see §8).

This keeps comparison at single-digit ms/pair and decode at one cached pass/episode. The accuracy cost of *N-1*-only is missing montage clips sourced from *N-2…N-k* (see §9).

---

## 8. Integration design

**Where it sits.** A new opt-in `CrossEpisodeReuseAnalyzer : IMediaFileAnalyzer` in the Recap chain, **after** ChapterAnalyzer (cheap regex/SponsorBlock wins first) and **replacing** the recap special-case currently bolted onto `ChromaprintAnalyzer` [[ChromaprintAnalyzer.cs:118-135]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L118-L135). It would run only when `ScanRecap` and ffmpeg are valid [[BaseItemAnalyzerTask.cs:361-365]](../../IntroSkipper/ScheduledTasks/BaseItemAnalyzerTask.cs#L361-L365), gated behind a new `DetectRecapUsingReuse` config flag (default off while experimental).

**Fingerprints it needs.**
* Query side: episode *N*’s opening. `(0, IntroFingerprintEnd)` is reusable as-is, or a tighter `(0, 120 s)` window.
* Reference side: prior episode’s **full** fingerprint `(0, Duration)`. This does **not** exist today and **cannot be reused** from `(0, IntroFingerprintEnd)` (opening only) or Credits `(CreditsFingerprintStart, End)` (tail only) — the reused clips are typically in the body. Requires a new fingerprint range.

**`GetFingerprintRange` / cache implications.** Add a dedicated full-episode range. Because the cache key is `(ItemId, Mode, Type, Start, End)` compared with `==` [[DetectionCacheService.cs:46-47]](../../IntroSkipper/FFmpeg/DetectionCacheService.cs#L46-L47), the cleanest options are:
  * (a) introduce a distinct reference range (e.g. a new `CacheEntryType` or a Recap-reference start/end of `(0, Duration)`) so it round-trips bit-exactly and never collides with the opening entry; **or**
  * (b) **promote Introduction to fingerprint the full episode** and have both Introduction and Recap read the same `(0, Duration)` entry — this *removes* a redundant decode (Intro already decodes `(0, 360)`) and gives recap its reference for free, at the cost of a larger Intro fingerprint. This is the most cost-efficient integration and worth a follow-up spike.

**`ConfigHasher` implications.** Add the new knobs (`DetectRecapUsingReuse`, query-window length, prior-episode count, `TopShifts`, `MinRunPoints`, `PreFilterMinOverlap`, montage gap) to the Recap analysis hash [[ConfigHasher.cs:44-49]](../../IntroSkipper/Helper/ConfigHasher.cs#L44-L49) so changing them invalidates stored recap segments; and to the Chromaprint detection-cache hash [[ConfigHasher.cs:78-79]](../../IntroSkipper/Helper/ConfigHasher.cs#L78-L79) **only if** the *fingerprint range* changes (the fingerprint bytes don’t depend on matching tuning, so matching knobs must stay out of the cache hash to avoid needless re-decodes).

**Prior selection / cross-season.** `QueueManager` groups by season, so *N-1* within a season is available; **season premieres** need the previous season’s finale, which the per-season queue doesn’t expose today — a queue change is required to handle S0xE01 recaps (see §9).

**Boundary.** Emit `Segment(N, hull)` with a non-zero start; optionally snap the end to the nearest black frame via the existing `BuildRecapFromBlackFrames` [[ChapterAnalyzer.cs:247-273]](../../IntroSkipper/Analyzers/ChapterAnalyzer.cs#L247-L273) as a refinement, then run the normal `TimeAdjustmentHelper`.

---

## 9. Failure modes (honest)

| Failure mode | Effect | Mitigation |
|---|---|---|
| **Re-mixed recap** (music bed / VO laid over clips) | Audio spectrum changes → fingerprint diverges > 6 bits → **miss**. The single biggest limit; many shows score their montages. | None on the audio path. Demonstrated by `FindReusedSpans_RejectsHeavilyAlteredAudio`. |
| **Re-encode / re-grade** of clips | Mild drift is tolerated by extraction (≤6 bits), **but shift *discovery* needs near-exact value matches** (`±IndexShift` in value space). If too few points survive near-exactly, the shift is never found. | Demonstrated by `FindReusedSpans_ToleratesMildReencodeNoise` (≈60 % survive ⇒ found). Consider a bit-tolerant LSH index for discovery if real data proves fragile. |
| **Shows that don’t reuse footage** (newly-shot “story so far”, animated recap) | No audio match → **no detection**. Fundamental to reuse matching (Prime Video shares this limit). | Pre-filter early-exits cheaply; fall back to chapter/black-frame signals. |
| **Season-premiere recap** of previous season | *N-1* is in a different season / absent → **miss**. | Extend prior selection across the season boundary (queue change). |
| **First episode of a series** | No prior → no recap. | Correct behaviour. |
| **Montage clips from *N-2…N-k*** | *N-1*-only comparison finds only the *N-1*-sourced clips → **under-segments**. | Compare against a small window of priors; cost scales linearly with *k*. |
| **Recurring non-recap shared audio** (theme, sponsor bumper, stock stinger reused across eps) | Could be matched as “reuse”. | The reuse-from-*body* signal (large, varied shifts + montage clustering) already distinguishes a recap from an opening bumper; add a min-total-reused-duration gate. |
| **Real-data spurious shifts** (correlated chromaprint points) | More candidate shifts than the synthetic 3–4. | `TopShifts` caps cost; vote-peak separation keeps quality (monitor on real media). |

---

## 10. Critical review of the current “earliest shared sting” recap

**What it actually does.** It fingerprints `(0, IntroFingerprintEnd)` of **both** episodes (opening-vs-opening) [[QueuedEpisode.cs:148]](../../IntroSkipper/Data/QueuedEpisode.cs#L148), finds shared audio regions with the intro machinery, takes the **earliest** one (≥ 3 s) [[ChromaprintAnalyzer.cs:293-328]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L293-L328), snaps its start to 0 [[ChromaprintAnalyzer.cs:317-325]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L317-L325), and then derives the recap **end** from the latest black frame before the intro [[ChromaprintAnalyzer.cs:255-291]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L255-L291).

**Why it is a *degenerate special case* of reuse matching.** A “Previously on Show X…” **bumper sting** (its title VO + music) is itself reused content that recurs at the **top of every episode**. Opening-vs-opening therefore matches the **bumper**, not the recap **clips** — and only because the bumper happens to sit in both episodes’ `(0, 360 s)` windows. The actual reused clips live in the prior episode’s **body**, which the current fingerprint range never covers (§2). So the method detects *“this episode has a recap bumper”* and then leans entirely on **black frames** for the extent.

**Shortfalls.**
1. **Conflates bumper with content.** A show that cuts straight into clips with **no** shared sting yields nothing, even though reuse matching would catch it.
2. **No audio extent.** The end is a black-frame heuristic; shows without a black frame between recap and cold open get a wrong or missing boundary.
3. **Start forced to 0** [[ChromaprintAnalyzer.cs:317-325]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L317-L325) mis-handles cold-open-then-recap ordering.
4. **“Earliest shared” ≠ recap.** The earliest shared opening region could be the main-title theme or a sponsor bumper; nothing verifies the region is *reused-from-elsewhere*, the defining recap property.
5. **Pairing fragility.** It needs two episodes that *both* carry the bumper near the top; partial-season or mixed-recap seasons pair poorly. The all-pairs loop [[ChromaprintAnalyzer.cs:90-180]](../../IntroSkipper/Analyzers/ChromaprintAnalyzer.cs#L90-L180) is also `O(E²)`.
6. **Shipped broken**, fixed only by adding the missing `GetFingerprintRange` case in `e17f044`; no real-media tests exist (`TestRecapDetection.cs` is synthetic).

**What to keep.**
* The **inverted-index + shift + contiguous-run** primitive is the right core; reuse matching extends it (with the corrected bounds of §4 and a multimap index).
* The **black-frame end snap** (`BuildRecapFromBlackFrames`) is a good *optional refinement* to land on a clean cut — keep it, demote it from primary signal.
* The config plumbing — `Min/MaximumRecapDetectionDuration`, `RecapCardMinimumDuration`, the Recap `ConfigHasher` case, `DetectRecapUsingBlackFrames`, `GetMaximumBoundaryAsync` — is reusable.
* The query-side window `(0, IntroFingerprintEnd)` is fine; what’s missing is the **prior episode’s full fingerprint** on the reference side.

---

## 11. Optional video corroboration (and why I recommend against it)

A coarse perceptual hash (downscaled grayscale **dHash** at low fps) could confirm reused **shots**. Honest assessment:

* **Strictly heavier than audio.** It requires decoding **video** frames (even at 1–2 fps, that’s image-scale decode + scaling + per-frame hashing) — categorically more than the audio-only path the plugin already pays for. It pushes directly against the “not heavy” constraint.
* **Audio already disambiguates reuse.** When the recap reuses the original audio, the audio fingerprint is a stronger, cheaper signal than low-fps dHash (which is coarse and prone to false matches on similar scenes/letterboxing/fades).
* **It only helps the case audio can’t.** The one scenario where video wins is a **re-dubbed / re-scored** recap (reused video, replaced audio) — rare. Even then, dHash at low fps is fragile to re-grading/cropping, exactly the transforms recaps apply.

**Verdict:** not worth it under the constraint. Video corroboration is strictly worse on cost and only marginally better on the rare re-scored case. If pursued, it should be a last-resort confirmation gated behind an explicit “heavy” opt-in, not part of the default path.

---

## 12. Honest verdict vs the current approach

| | Current (earliest shared sting) | RFC B (cross-episode reuse) |
|---|---|---|
| Detects recaps **without** a shared bumper | ❌ | ✅ |
| Measures recap **extent from audio** | ❌ (black-frame guess) | ✅ (reused-span hull) |
| Correct **non-zero start** | ❌ (snaps to 0) | ✅ |
| Distinguishes recap from theme/bumper | ❌ | ✅ (reuse-from-body + montage) |
| Handles **re-mixed** recaps | ❌ | ❌ (fundamental audio limit) |
| Handles shows that **don’t reuse** footage | ❌ | ❌ (fundamental) |
| Season-premiere recaps | ❌ | ⚠️ needs cross-season queue |
| Extra decode cost | opening only (~1.1 s) | **full prior episode (~4.6 s, cached)** |
| Comparison CPU | intro-scale | **2.9–7.4 ms/pair** (Debug) |
| GPU | none | none |

**Bottom line:** RFC B is a genuine accuracy upgrade for the common “reused original audio” recap and removes the bumper/black-frame dependency, at the cost of one cached full-episode audio decode per episode and a structural fix to the contiguous matcher. It is **not** a silver bullet — re-mixed recaps and non-reusing shows remain undetectable by any audio method, and season boundaries need a queue change. It comfortably meets “not CPU/GPU heavy” on the audio path; video corroboration does not and should be avoided.

---

## 13. Prototype & reproduction

Files (this branch):
* `IntroSkipper/Analyzers/CrossEpisodeReuseMatcher.cs` — algorithm (pre-filter, multimap index, shift voting, corrected extraction, montage assembly). Self-contained; reuses `ChromaprintAnalyzer.CountBits` and `ChromaprintConstants.SampleDuration`.
* `IntroSkipper/Analyzers/ReuseMatchOptions.cs`, `ReusedSpan.cs`, `ReuseMatchDiagnostics.cs` — supporting types.
* `IntroSkipper.Tests/TestCrossEpisodeReuse.cs` — 8 tests: single planted span, mild-reencode tolerance, heavy-alteration rejection, **3-clip montage assembly**, no-match early-exit, **production-limitation demo**, hop-rate sanity, and the realistic-size **microbenchmark**.

Run:
```bash
cd /home/intro-skipper/web && pnpm install --frozen-lockfile && pnpm build
cd /home/intro-skipper && dotnet test IntroSkipper.Tests/IntroSkipper.Tests.csproj -p:SkipWebBuild=true \
  --filter FullyQualifiedName~TestCrossEpisodeReuse
# benchmark table is written to $TMPDIR/recap-rfc-b-bench.md
```

**Result:** 8/8 spike tests pass; full suite **323 passed, 1 failed, 324 total**. The single failure is the pre-existing `TestAudioFingerprinting.TestSilenceDetection`, an environmental artifact (system ffmpeg 6.1.1 emits 3-decimal silence timestamps vs the 6-decimal literals baked against jellyfin-ffmpeg7) — unrelated to recap and present on a pristine checkout.

---

## 14. Appendix — measured data (this VM)

**Full-episode chromaprint decode** (synth 24-min/1440 s audio, `ffmpeg -ac 2 -f chromaprint -fp_format raw`): 4.08 s / 4.91 s / 4.64 s ⇒ median **4.6 s**, 11,614 points, **≈310× realtime**.

**Comparison microbenchmark** (Debug, 200 iters/size):

| prior-episode size | distinct shifts | shifts scanned | spans | avg ms/pair |
|---|---|---|---|---|
| 10,659 pts | 4 | 4 | 3 | 2.86 |
| 11,628 pts | 3 | 3 | 3 | 4.82 |
| 20,349 pts | 3 | 3 | 3 | 4.74 |
| 29,070 pts | 3 | 3 | 3 | 7.45 |
