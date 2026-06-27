# RFC A — Subtitle / caption phrase detection for Recap segments

**Status:** Research + design spike. **Not** a mergeable feature. No analyzer is wired into the
runtime chain; this RFC ships a unit-tested pure core plus an ffmpeg-gated end-to-end spike that
proves the extraction mechanism, and a design for how it would integrate.

**Author:** recap research spike • **Target:** `intro-skipper/intro-skipper` branch `10.11`
**ffmpeg verified against:** `ffmpeg 6.1.1` (Ubuntu) — `subrip`/`mov_text`/`webvtt`/`ass` encoders + `ffprobe` present.

---

## TL;DR verdict

Subtitle phrase detection is the **highest-precision, cheapest** recap cue available, and it fixes
the two structural mistakes in the current recap code: (1) it can find recaps that **don't start at
0** (after a cold open / after the intro), which the current code structurally cannot, and (2) it
needs **no cross-episode comparison**, so it works on a single episode, season 1 episode 1, and
shows with no shared "Previously on" sting.

It is **not** a universal solution: it only works when a **text** subtitle stream (or sidecar) exists
and actually transcribes the recap voiceover/cards. Image subs (PGS/VOBSUB/DVBSUB) and
no-subtitle content fall through to the existing audio/black-frame signals. So the honest framing is
**"a new high-precision first pass in the Recap chain, with the current detectors as fallback"** —
not a replacement.

What is **proven** in this spike (real ffmpeg, see [§0](#0-what-was-actually-built-and-verified)) vs
**asserted** (design only) is called out explicitly throughout.

---

## 0. What was actually built and verified

### Prototype (committed, compiles under the repo's strict analyzers, unit-tested)

Pure, I/O-free core under `IntroSkipper/Subtitles/`:

| File | Responsibility |
|---|---|
| `IntroSkipper/Subtitles/SubtitleCue.cs` | `record SubtitleCue(double Start, double End, string Text)` |
| `IntroSkipper/Subtitles/SubtitleParser.cs` | Parse SubRip / WebVTT / ASS payloads → cues (lenient; BOM/CRLF/cue-settings tolerant) |
| `IntroSkipper/Subtitles/RecapPhraseMatcher.cs` | Normalize (case/diacritics/markup/punct) + **anchored** multilingual phrase match |
| `IntroSkipper/Subtitles/SubtitleRecapSegmentBuilder.cs` | **The core**: cues + matcher + options + black frames → `SubtitleRecapResult?` |
| `IntroSkipper/Subtitles/SubtitleRecapOptions.cs` / `SubtitleRecapResult.cs` | Tunables / result record |
| `IntroSkipper/Subtitles/SubtitleCodec.cs` | TEXT vs IMAGE `codec_name` classification (grounded in real ffmpeg codec strings) |
| `IntroSkipper/Subtitles/SubtitleStreamInfo.cs` / `SubtitleProbe.cs` | `ffprobe -show_streams` JSON → typed stream list + classification |

Tests:

- `IntroSkipper.Tests/TestSubtitleRecapDetection.cs` — **58 pure unit tests**, no media: parser
  (SRT/VTT/ASS, BOM, CRLF, multi-line, cue settings, short timestamps), matcher (English +
  ES/PT/FR/DE/IT/JA, diacritics, anchoring, **false-positive rejection**), builder (anchor, cluster
  growth, gap stop, black-frame snap, min/max clamp, window guard, opt-in start-to-0), codec
  classification, ffprobe JSON parsing.
- `IntroSkipper.Tests/TestSubtitleRecapSpike.cs` — **1 ffmpeg-gated end-to-end test**
  (`[FactSkipFFmpegTests]`): muxes a synthetic clip → enumerates streams with ffprobe → extracts the
  opening window as SRT → detects the black frame → runs the **real** builder. Asserts
  `Start≈2.0` (not 0) and `End≈10.0` (snapped to the fade-to-black).

Test result on this environment: **374 passed, 1 failed, 0 skipped**. The single failure
(`TestAudioFingerprinting.TestSilenceDetection`) is **pre-existing and environmental** — an ffmpeg
`silencedetect` floating-point rounding difference (`44.631042` expected vs `44.631` actual) present
on the pristine `10.11` checkout, unrelated to this work. My 59 added tests all pass.

### End-to-end ffmpeg proof (`docs/recap-research/spike/extract_spike.sh`)

The committable script (run with `bash docs/recap-research/spike/extract_spike.sh`) muxes a 40 s
synthetic clip with an embedded `subrip` track ("Previously on…") plus a fade-to-black in `[10,11] s`,
then proves each pipeline step. **Verbatim observed output:**

```
==> (2) ffprobe: enumerate subtitle streams (index, codec, language)
{ "streams": [ { "index": 1, "codec_name": "subrip", "codec_type": "subtitle",
                 "tags": { "language": "eng" } } ] }

==> (3) extract ONLY the opening 15s as SubRip to stdout (mkv/subrip)
1
00:00:02,000 --> 00:00:05,000
Previously on Test Show...

2
00:00:05,500 --> 00:00:09,000
...the hero lost everything.
            (cue #3 at 00:00:30 is correctly excluded by the 15s window)

==> (4) black-frame detection in [0,20] (plugin filter: blackframe=amount=50:threshold=28)
[Parsed_blackframe_0 @ ...] frame:50 pblack:100 pts:10000 t:10.000000 ...
[Parsed_blackframe_0 @ ...] frame:51 pblack:100 pts:10200 t:10.200000 ...
```

This de-risks the entire mechanism: **mux → probe (codec+lang) → cheap windowed text extraction →
black-frame boundary** all work with stock ffmpeg 6.1.1 and feed the production parser/builder.

**Verified vs assumed** is tabulated in [§11](#11-verified-vs-assumed).

---

## 1. Subtitle sources

### 1.1 Text vs image streams

Only **text** subtitle codecs can be transcribed cheaply; image (bitmap) subs require OCR and are
**out of scope** for phrase detection. The classifier (`SubtitleCodec.cs`) uses the exact
`codec_name` strings ffprobe reports, taken from the local ffmpeg decoder list:

- **TEXT (in scope):** `subrip`, `srt`, `mov_text`, `webvtt`, `ass`, `ssa`, `text`, `subviewer`,
  `sami`, `microdvd`, `mpl2`, `jacosub`, `pjs`, `realtext`, `vplayer`, `stl`, `eia_608`/`cc_dec`
  (CEA-608 closed captions).
- **IMAGE (skip — need OCR):** `hdmv_pgs_subtitle` (PGS), `dvd_subtitle` (VOBSUB/DVDSUB),
  `dvb_subtitle`/`dvbsub` (DVBSUB), `xsub`, `dvb_teletext`.

Unknown/future codecs are treated as **non-text** (conservative: we never feed garbage to the
matcher). Sidecar files are classified by extension → `.srt`/`.vtt`/`.ass`/`.ssa` are text and parsed
directly (no ffmpeg call); `.sup`/`.idx`+`.sub` are image and skipped.

### 1.2 Enumerating streams (ffprobe)

The plugin **already shells out to ffprobe** — see `FFmpegService.ProbeAudioDurationAsync`
(`IntroSkipper/FFmpeg/FFmpegService.cs:322-368`) and ffprobe-path resolution
`GetFFprobePath` (`IntroSkipper/FFmpeg/FFmpegService.cs:490-502`). Subtitle enumeration reuses
exactly that pattern:

```
ffprobe -v error -select_streams s \
  -show_entries stream=index,codec_name,codec_type,disposition:stream_tags=language \
  -of json <path>
```

`SubtitleProbe.Parse` turns that JSON into `SubtitleStreamInfo { Index, Codec, Language, IsTextBased,
IsForced }`. `IsForced` is useful because **forced** subtitle tracks often carry the on-screen
"Previously on" card text even when the main dialogue track is image-based.

### 1.3 Extracting just the opening window (ffmpeg)

We never extract the whole file. We map a single subtitle stream and bound it with `-to`:

```
ffmpeg -hide_banner -loglevel error -i <path> -to <window> -map 0:s:<idx> -f srt -
```

- `-map 0:s:<idx>` selects **only** the subtitle stream → video/audio are not decoded.
- `-to <window>` (e.g. 150 s) reads only the opening of the container.
- `-f srt` normalizes every text codec (`mov_text`, `webvtt`, `ass`, …) into one grammar, so the hot
  path is a single SubRip parser. **Verified** for both `subrip` (MKV) and `mov_text` (MP4) in §0.

External sidecars are read off disk and parsed directly by `SubtitleParser` (no ffmpeg), with
`SubtitleParser.DetectFormat` choosing the SRT/VTT/ASS branch.

### 1.4 Jellyfin context

"Jellyfin extracts subtitles on demand, not during indexing," so the plugin must do its own
extraction — which is exactly what §1.2/§1.3 do via the existing `FFmpegService`/`FFmpegProcess`
plumbing (`IntroSkipper/FFmpeg/FFmpegProcess.cs:42-118`). Optionally, if
`jellyfin-plugin-subtitleextract` has already written a sidecar, we can prefer that file and skip the
ffmpeg call entirely.

---

## 2. Phrase matching

### 2.1 Normalization (`RecapPhraseMatcher.Normalize`)

Cue text is normalized before comparison so casing, diacritics, and markup never block a match:

1. strip HTML/VTT tags and ASS override blocks (`<i>`, `<c.x>`, `{\an8}`);
2. Unicode **NFD decompose** and drop combining marks → `Précédemment` → `precedemment`;
3. map punctuation/symbols to spaces (so `previously,` ≈ `previously`);
4. collapse whitespace; lower-case (`ToLowerInvariant`).

### 2.2 Anchoring (the precision lever — and a finding)

Naïve "phrase appears anywhere in the cue" is too loose. My **first** implementation used a
character-offset tolerance (allow the phrase to start within N chars). A unit test immediately
falsified it: `"I told you previously on Tuesday that we should leave"` has `"previously on"` at
char 11, indistinguishable by offset from a legitimate `"[NARRATOR] Previously on…"`.

The fix (kept) strips **structured** leading noise on the raw text before matching
(`RecapPhraseMatcher.StripLeadingNoise`): a single leading bracketed/parenthetical sound cue
(`[NARRATOR]`, `(theme music)`), one `NAME:` speaker label, dialogue dashes, and quote/♪ glyphs —
then requires the phrase at the **start** (tolerance 2). Result:

- ✅ `"[NARRATOR] Previously on the show"`, `"- Previously on…"`, `"JOHN: Previously on…"` → match
- ❌ `"I told you previously on Tuesday…"` → **rejected** (no strippable prefix; doesn't start with the phrase)

This is exactly the kind of edge that a "matches 'previously on' in subtitles" one-liner gets wrong;
the prototype encodes the precise rule and tests it.

### 2.3 Default phrase list (multilingual, precision-biased)

`RecapPhraseMatcher.DefaultPhrases` (configurable seed):

| Lang | Phrases |
|---|---|
| English | `previously on`, `last time on`, `last week on`, `last season on`, `earlier this season` |
| Spanish | `anteriormente en`, `en episodios anteriores` |
| Portuguese | `anteriormente em` |
| French | `précédemment dans`, `précédemment sur` |
| German | `was bisher geschah`, `was bisher passierte`, `bisher bei` |
| Italian | `negli episodi precedenti`, `nelle puntate precedenti` |
| Dutch | `wat voorafging` |
| Japanese | `前回`, `これまでの` |
| Korean | `지난 이야기` |

Deliberately **multi-word** for English (`previously on`, not bare `previously`) to avoid matching
`"Previously unseen footage…"`. This is a recall/precision trade documented in [§7](#7-limits-failure-modes-and-where-it-breaks).
Prior art (`Hellowlol/bw_plex`) matches `previously on` / `last season` / `last episode`; this list
is a superset with stricter anchoring.

### 2.4 End cue

End cues are rare in subtitles (a recap montage just stops). The primary end mechanism is the
**dense-cluster + black-frame snap** in §3, not a phrase. The builder is structured so an optional
end-phrase list could short-circuit cluster growth later, but it is intentionally not relied upon.

---

## 3. Boundary construction (`SubtitleRecapSegmentBuilder.Build`)

Given parsed `cues`, a `matcher`, `SubtitleRecapOptions`, and optional `blackFrameTimes`:

1. **Anchor.** First cue (by start) with `Start ≤ MaxWindowSeconds` whose text matches a recap
   opening. `Start = anchorCue.Start` — **not forced to 0**. No match → `null`.
2. **Grow cluster.** Absorb subsequent cues while `nextCue.Start − end ≤ MaxClusterGapSeconds`
   (default 12 s). `End = last absorbed cue.End`. The first large gap (cold open / dialogue) ends it.
3. **Black-frame snap.** If black-frame times are supplied, snap `End` to the nearest one in
   `[end − 1 s, end + BlackFrameSnapSeconds]` (default forward window 6 s) — the montage fade-out.
4. **Optional start-to-0 snap.** Only if `SnapStartToZero` (opt-in) **and** `Start ≤ StartSnapSeconds`.
5. **Clamp & validate.** Clamp duration to `MaxDurationSeconds`; reject if `< MinDurationSeconds`.

Returns `SubtitleRecapResult { Start, End, MatchedPhrase, AnchorCueText, CueCount,
SnappedToBlackFrame }`.

### On-screen card vs subtitle

- If the "Previously on" text is a **subtitle cue** (incl. forced track) → handled directly.
- If it's a **burned-in on-screen card** with no cue → there is no text to match; this case falls
  through to the existing chromaprint/black-frame detectors. The forced-track path (§1.2) recovers
  many of these because studios frequently ship the card as a forced subtitle.

### Why End uses the existing black-frame pass

Reusing `FFmpegService.DetectBlackFramesAsync` (`IntroSkipper/FFmpeg/FFmpegService.cs:166-199`) +
`FFmpegOutputParser.ParseBlackFrames` (`IntroSkipper/FFmpeg/FFmpegOutputParser.cs:76-101`) means the
end boundary is **frame-accurate at the fade**, and the subtitle layer only has to get the
*neighborhood* right. The spike test exercises this exact parser on real ffmpeg output.

---

## 4. Integration into this codebase

### 4.1 New analyzer in the Recap chain

Add `SubtitleRecapAnalyzer : IMediaFileAnalyzer` (`IntroSkipper/Analyzers/IMediaFileAnalyzer.cs:14-27`).
Per-episode (no cross-episode comparison): enumerate streams → pick best text stream (prefer
`Language` ∈ configured filter, prefer `forced`) → extract opening window → parse → `Build(...)` with
black frames from `DetectBlackFramesAsync` → on success, `UpdateTimestampAsync(seg,
AnalysisMode.Recap, …)` and `SetAnalyzed(Recap, Analyzed)`. Episodes it can't handle are returned
unanalyzed for the next analyzer (the interface contract).

### 4.2 Precedence

In `BaseItemAnalyzerTask.AnalyzeItemsAsync` the Recap branch currently builds
`[ChapterAnalyzer, ChromaprintAnalyzer]` (`IntroSkipper/ScheduledTasks/BaseItemAnalyzerTask.cs:324-365`).
Recommended order:

```
ChapterAnalyzer        (existing; explicit "Recap" chapter wins — cheapest, most authoritative)
SubtitleRecapAnalyzer  (new; high precision, finds non-zero starts, single-episode)
ChromaprintAnalyzer    (existing; shared sting fallback)
```

Slot the `new SubtitleRecapAnalyzer(...)` between lines `327` and `364`. It must also be reachable by
the `AnalyzerAction` promotion switch (`BaseItemAnalyzerTask.cs:372-390`) — add an
`AnalyzerAction.Subtitle` (or reuse `Chapter`-like promotion) so a season can be pinned to it.
Because each analyzer skips already-`Analyzed` episodes via `NeedsAnalysis`
(`IntroSkipper/Data/QueuedEpisode.cs:131-132`), ordering is pure priority with no double work.

### 4.3 Movies

Recaps are a TV concept; gate the analyzer to non-movie like the existing Recap branch
(`BaseItemAnalyzerTask.cs:361`).

### 4.4 `TimeAdjustmentHelper` interaction (important)

`AdjustIntroTimesAsync` snaps **start → 0** whenever `rawStart ≤ EndSnapThreshold` (default 2 s)
(`IntroSkipper/Analyzers/TimeAdjustmentHelper.cs:64-89`). For a recap that opens the episode this is
fine, but for a recap **after a cold open** (start e.g. 75 s) we must preserve the real start. Two
options:

- **Preferred:** run the subtitle result through a recap-aware path that applies only **end**
  adjustments (keyframe/silence snap, `TimeAdjustmentHelper.cs:96-132`) and leaves start untouched, or
- pass a flag to skip the `≤ EndSnapThreshold` start-snap for `AnalysisMode.Recap`.

The builder already finalizes End via black-frame snap, so keyframe-snapping the end is a nice-to-have,
not required. Either way the subtitle analyzer must **not** inherit the "force start to 0" behavior of
the existing recap detectors (see [§9](#9-critical-review-of-the-current-recap-implementation)).

### 4.5 Config surface (`PluginConfiguration`)

Add alongside the existing recap settings (`IntroSkipper/Configuration/PluginConfiguration.cs:254-276`):

| Setting | Default | Purpose |
|---|---|---|
| `DetectRecapUsingSubtitles` | `false` (spike) → `true` (ship) | master switch for the analyzer |
| `RecapSubtitlePhrases` | `RecapPhraseMatcher.DefaultPhrases` joined | newline/`;`-separated phrase list |
| `RecapSubtitleLanguages` | empty (= any) | ISO-639 filter, e.g. `eng,spa` |
| `RecapSubtitleMaxWindowSeconds` | `150` | scan window / `MaxWindowSeconds` |
| `RecapSubtitleClusterGapSeconds` | `12` | `MaxClusterGapSeconds` |

Reuse existing `MinimumRecapDuration`/`MaximumRecapDuration` (`PluginConfiguration.cs:254-261`) for
the builder's min/max. UI: a checkbox + textarea + language box on the existing Recap section of
`IntroSkipper/Configuration/configPage.html` (web source under `web/`, built to
`IntroSkipper/Configuration/introskipper.{js,css}`).

### 4.6 Cache + `ConfigHasher`

- **Analysis hash:** extend the `AnalysisMode.Recap` arm of `ConfigHasher.Analysis`
  (`IntroSkipper/Helper/ConfigHasher.cs:44-49`) to include the five new settings (a stable hash of the
  phrase list, language filter, window, gap, enable flag). This makes a phrase-list edit correctly
  invalidate stored recap segments and re-trigger analysis via `QueueManager.VerifyQueueAsync`
  (`IntroSkipper/Manager/QueueManager.cs:461-490`).
- **Extraction cache (optional):** extraction is so cheap (§8) that a dedicated
  `CacheEntryType.Subtitle` (`IntroSkipper/Data/CacheEntryType.cs`) is optional. If added, key it by
  `(itemId, Recap, Subtitle, 0, window)` and hash on the language filter only, mirroring
  `ConfigHasher.DetectionCache` (`ConfigHasher.cs:72-96`). Black-frame results are **already** cached
  by the existing `CacheEntryType.BlackFrame` path, so the snap step is free on re-runs.

### 4.7 `SegmentProvider`

No change. `AnalysisMode.Recap → MediaSegmentType.Recap` already maps
(`IntroSkipper/Providers/SegmentProvider.cs:28`), so a subtitle-derived recap surfaces to clients as
`Recap` (Skip/AskToSkip) like any other.

---

## 5. Algorithm pseudocode

```text
analyze(episode):
    if not config.DetectRecapUsingSubtitles: return  # leave for next analyzer

    streams = SubtitleProbe.Parse(ffprobe_show_streams(episode.path))
    text = pick(streams where IsTextBased,
                prefer language in config.languages, prefer forced, else first)
    if text is none:
        if sidecar(episode.path) exists and is text: payload = read(sidecar)
        else: return                                  # fall through to chromaprint/blackframe
    else:
        payload = ffmpeg_extract_srt(episode.path, stream=text.Index, to=window)   # text-only, cheap

    cues = SubtitleParser.Parse(payload)
    blackTimes = DetectBlackFramesAsync(episode, [0, window]).map(f -> f.Time)      # cached, reused
    result = SubtitleRecapSegmentBuilder.Build(cues, matcher, options, blackTimes)
    if result is null: return

    seg = Segment(episode.Id, result.Start, result.End)   # NOT forced to 0
    seg = adjustEndOnly(seg)                               # keyframe/silence end-snap; keep start
    UpdateTimestampAsync(seg, Recap); SetAnalyzed(Recap, Analyzed)
```

`Build` (the unit-tested core) is the pseudocode of §3, verbatim in
`IntroSkipper/Subtitles/SubtitleRecapSegmentBuilder.cs`.

---

## 6. Config / UI / hash implications (summary)

- New `PluginConfiguration` fields (§4.5) → must be added to the embedded `configPage.html` + the
  `web/` TS source and rebuilt (`pnpm build`).
- `ConfigHasher.Analysis(…, Recap, …)` must include them (§4.6) or edits silently won't re-analyze.
- `RecapDetectionHelper.GetMaximumBoundaryAsync` (`IntroSkipper/Analyzers/RecapDetectionHelper.cs:21-36`)
  already computes a sensible scan ceiling (`min(duration, MaximumRecapDetectionDuration, intro.Start)`);
  the subtitle window should adopt the same ceiling so subtitle/chromaprint/blackframe agree on "how
  far into the episode a recap can be."

---

## 7. Limits, failure modes, and where it breaks

Honest failure catalogue — this is where subtitles are **not** the answer:

1. **No text subtitles.** Image-only (PGS/VOBSUB/DVB) or no subs at all → analyzer returns nothing,
   chromaprint/black-frame fallback runs. This is common for older/disc rips and some anime.
2. **Recap not transcribed.** Some subtitle tracks omit the "Previously on" voiceover or the recap
   montage entirely (forced-narrative tracks especially) → no cue to match → miss.
3. **On-screen card only.** Burned-in card with no subtitle/forced track → miss (falls through).
4. **Foreign-language-only subs vs phrase list.** If the only text track is a language not in the
   default list, no match. Mitigation: broad default list + user-extensible phrases + language filter.
5. **Subtitle timing offset / desync.** Sidecar `.srt` mistimed by a few seconds shifts Start/End;
   the black-frame end-snap absorbs small drift but not gross desync.
6. **False positives.** A mid-episode "previously on …" line could match — mitigated by
   `MaxWindowSeconds`, anchored-at-start matching, and multi-word phrases (§2.2/§2.3). Residual risk:
   a show whose cold open literally opens with someone saying "Last time on …" in dialogue.
7. **CEA-608 caption quirks.** `eia_608` extraction needs `-f srt` over the decoded captions and can
   be noisy (all-caps, positioning). Classified as text but flagged lower-confidence.
8. **Cluster mis-growth.** If recap cues and the first cold-open line are <12 s apart, the cluster can
   over-extend; the black-frame snap usually corrects the end, but `MaxClusterGapSeconds` is the lever.

Items 1–3 are **fundamental** (no signal in the modality) → subtitles must be a *layer*, not a
replacement. Items 4–8 are tunable.

---

## 8. Performance vs the "not CPU/GPU heavy" constraint

The maintainers' original constraint (issue #136) was "find a way that's not CPU/GPU heavy."
Subtitle detection is the **cheapest** signal in the plugin:

| Step | Work | Relative cost |
|---|---|---|
| `ffprobe -show_streams` | container header parse | trivial (already done for audio duration, `FFmpegService.cs:322`) |
| `ffmpeg -map 0:s -to N -f srt` | demux + **subtitle-only** transcode of opening window; **no** video/audio decode | very low — sub-second in the spike |
| parse + normalize + match + build | pure CPU over a few dozen short strings | negligible (µs–ms) |
| black-frame snap | **reuses** the cached `BlackFrame` pass | free on re-runs (`FFmpegService.cs:176-180`) |

Contrast with the current recap detectors: chromaprint **decodes and resamples audio** for the whole
analysis window of *every* episode and does O(n²) cross-episode comparison
(`ChromaprintAnalyzer.AnalyzeMediaFiles`), and the black-frame fallback **decodes video frames**.
Subtitle detection decodes neither audio nor video. It comfortably satisfies the constraint and is a
strict CPU win when it succeeds (it can let chromaprint be skipped for that episode).

Caveat (honest): the **first** extraction still spawns one ffmpeg process per episode (process
start-up dominates the cost). With ~dozens of ms of subtitle transcode that's still far below a
chromaprint pass, and results are cacheable.

---

## 9. Critical review of the current recap implementation

Read through the subtitle lens, with what to keep.

### What it gets wrong

1. **Start is structurally forced to 0 — recaps after a cold open are mis-bounded.**
   - Chromaprint recap: `GetEarliestTimeRange` sets `Start = 0` whenever the shared region starts
     `≤ 5 s` (`IntroSkipper/Analyzers/ChromaprintAnalyzer.cs:317-325`), and
     `BuildRecapFromChromaprintCandidateAsync` builds `[0, blackframe]`
     (`ChromaprintAnalyzer.cs:255-291`).
   - Chapter/black-frame recap: `BuildRecapFromBlackFrames` **always** returns
     `new Segment(id, new TimeRange(0, …))` (`IntroSkipper/Analyzers/ChapterAnalyzer.cs:247-273`,
     esp. `:272`).
   - The task description itself notes recaps can appear "before a cold open, after a cold open, or
     after the intro." The current code can only express the first. Subtitle anchoring yields the
     **true** start (`SubtitleRecapResult.Start`), and `Build` only snaps to 0 when explicitly opted
     in (§3.4).

2. **Requires cross-episode audio similarity it often won't have.** The chromaprint recap finds the
   *earliest shared* audio region across episodes (the sting), min 3 s
   (`ChromaprintAnalyzer.cs:26,221-222`). Recaps are *different every episode* by definition; the only
   shared audio is a short "Previously on" sting/music bed that many shows **don't have**. It also
   needs ≥2 episodes and is weak on S01E01. Subtitles need none of this — single episode, episode 1,
   no sting required.

3. **"Earliest shared region" is a fragile proxy for "recap".** Any early shared audio (a network
   bumper, a cold-open stinger, a shared SFX) can be selected as the recap card, then the segment is
   stretched to the next black frame. There's no semantic check that the region *is* a recap.
   Subtitle text (`"Previously on"`) is a **semantic** cue, not a coincidental-audio cue.

4. **Black-frame-only fallback picks the latest black frame before a boundary, with start 0**
   (`ChapterAnalyzer.cs:247-273`) — i.e. it assumes the recap is "everything from 0 to the last early
   fade," which over-captures a cold open that ends in a fade and has no recap at all.

5. **Shipped unvalidated.** Recap (PR #771) shipped broken — `GetFingerprintRange` had no `Recap`
   case so fingerprinting threw and aborted; the `(0, IntroFingerprintEnd)` case was added later
   (`IntroSkipper/Data/QueuedEpisode.cs:143-152`). All recap tests are synthetic
   (`TestRecapDetection.cs`, `TestChapterAnalyzer.cs`) — there is **no** real-media recap test. This
   spike adds the first real-media (ffmpeg) recap-path test, albeit for the subtitle approach.

### What to keep

- **Black-frame detection + parser** (`FFmpegService.DetectBlackFramesAsync` `:166-199`,
  `FFmpegOutputParser.ParseBlackFrames` `:76-101`) — excellent **end-boundary** snapper; the subtitle
  builder reuses it directly and the spike proves it.
- **`RecapDetectionHelper.GetMaximumBoundaryAsync`** (`:21-36`) — the `min(duration,
  MaximumRecapDetectionDuration, intro.Start)` ceiling is a good shared scan bound; subtitles should
  adopt it.
- **Chromaprint recap as a fallback** — for shows with a genuine shared "Previously on" sting and no
  usable subtitles, it's still the best available signal. Keep it *after* subtitles in the chain.
- **The analyzer-chain design** (`BaseItemAnalyzerTask.cs:324-398`) — ordered `IMediaFileAnalyzer`s
  with `NeedsAnalysis` short-circuiting is exactly the right shape to slot a subtitle analyzer in.
- **Chapter recap regex** (`PluginConfiguration.cs:369-370`) — when an explicit "Recap" chapter
  exists it's the most authoritative and cheapest; keep it first.

---

## 10. Pros / cons / risk vs the current implementation

**Pros**
- Finds recaps with **correct, non-zero starts** (the current code can't).
- **Single-episode**, works on S01E01 and shows with no shared sting; no O(n²) comparison.
- **Semantic** cue ("Previously on") → high precision; far fewer false "early shared audio" hits.
- **Cheapest** signal; decodes neither audio nor video; satisfies the "not heavy" constraint and can
  save a chromaprint pass.
- Reuses existing ffprobe/ffmpeg/black-frame plumbing and the cache.

**Cons**
- Only works with **text** subs that **transcribe** the recap (§7.1–7.4). Coverage is content-dependent.
- One ffmpeg process per episode on first run (cheap, but non-zero).
- New config surface, hash entries, and a language/phrase list to maintain.

**Risks**
- **Coverage gap risk (medium):** users with image-only or sub-less libraries see no benefit → must
  be layered with the existing detectors, never a replacement. Mitigated by analyzer ordering.
- **False-positive risk (low):** mitigated by window + anchoring + multi-word phrases; residual on
  shows whose dialogue opens with "Last time on…".
- **Maintenance risk (low):** phrase list per language; community-extensible via config.
- **Integration risk (low–medium):** the `TimeAdjustmentHelper` start-snap (§4.4) must be bypassed for
  recap or it re-introduces the "force start to 0" bug for post-cold-open recaps.

**Verdict:** Implement as a **new first-class layer** in the Recap chain (after Chapter, before
Chromaprint), defaulting **on** where a text track exists and silently deferring otherwise. It
directly fixes the start-at-0 and cross-episode-dependence defects while being the cheapest detector
in the plugin. It is **not** a standalone replacement.

---

## 11. Verified vs assumed

| Claim | Status | Evidence |
|---|---|---|
| ffmpeg can mux text subs (subrip/mov_text) into MKV/MP4 | **Verified** | `extract_spike.sh` step 1b; spike test |
| ffprobe enumerates subtitle `index`/`codec_name`/`language` | **Verified** | spike output §0; `SubtitleProbe` test |
| Opening window extractable as SRT to stdout, text-only, cheap | **Verified** | spike output §0 (subrip + mov_text) |
| `-to N` excludes later cues (windowing works) | **Verified** | cue #3 @30 s excluded by 15 s window |
| Black frame at recap boundary detected by the plugin's filter/parser | **Verified** | spike output §0; `FFmpegOutputParser.ParseBlackFrames` in spike test |
| Image-sub `codec_name` strings to skip (PGS/VOBSUB/DVB/xsub) | **Verified** | ffmpeg `-decoders` list; classifier unit tests |
| Pure parse→match→build core (anchor, cluster, snap, clamp, window) | **Verified** | 58 unit tests |
| End-to-end real-ffmpeg pipeline → correct `Start≈2`, `End≈10` | **Verified** | `TestSubtitleRecapSpike` |
| Analyzer wiring / precedence / config / hash / UI | **Designed (assumed)** | §4–§6 — not implemented in this spike |
| Real-world recall across diverse shows/languages | **Assumed** | needs a media corpus; not measured here |
| Synthesizing a real PGS/VOBSUB bitmap stream in-env | **Not done** | ffmpeg can't transcode text→bitmap; classification verified on codec strings instead |

---

## Appendix — reproduce

```
# pure core + classifier + ffprobe parser (no media)
dotnet test IntroSkipper.Tests/IntroSkipper.Tests.csproj -p:SkipWebBuild=true \
  --filter FullyQualifiedName~TestSubtitleRecapDetection

# end-to-end ffmpeg spike (needs ffmpeg on PATH)
dotnet test IntroSkipper.Tests/IntroSkipper.Tests.csproj -p:SkipWebBuild=true \
  --filter FullyQualifiedName~TestSubtitleRecapSpike

# shell reproduction of the extraction pipeline
bash docs/recap-research/spike/extract_spike.sh
```
