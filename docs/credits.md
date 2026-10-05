# Credits

How the credits pass turns each episode's evidence into stored Outro segments, and how to judge a change to it. Terms such as roll credits, card credits, page, keyframe scan and card kind are defined in `CONTEXT.md`. The exact rules live in the XML docs of the classes named here; this page explains how they fit together and why.

## The credits window

Each episode's window ends at its duration, or at its audio stream's end when `ProbeAudioDuration` is on and the audio is shorter. It starts `MaximumCreditsDuration` before that, or `MaximumMovieCreditsDuration` for a movie. The general analysis percentage does not apply, since it can cut off the real credits boundary. Every episode in the season gets a window, settled ones included, because Chromaprint compares against their cached fingerprints under the same window.

## The credits pass

`CreditsPass` analyzes a season. Unlike the other modes, which stop at the first analyzer that settles an episode, it combines every analyzer's candidate per episode, because credits come in parts that different evidence finds: a styled section that only shared audio catches, a roll on black, a card run, a dubbing card after an epilogue.

- **Chapter authority.** By default a recognized credits chapter settles the episode, with the chapter analyzer's duration rules and the configured time adjustments, and no combined detection runs for it. Having chapters is not enough: when no credits chapter matches, or a match fails the chapter duration limits, the other candidates are combined.
- **Enhanced chapters.** With `EnhanceChapterCredits` on (off by default), chapter matches are combined with the other candidates instead. That can extend the credits or find more credits blocks, such as dubbing cards after an epilogue that an "ending" chapter leaves out, at the cost of more analysis and less faithful authored boundaries.
- **Candidates.** The keyframe analyzer contributes black rolls and at most one card run from one keyframe scan. The season-wide Chromaprint comparison contributes shared credits audio. Chapter matches contribute under enhancement. With `UseLegacyBlackFrameAnalyzer` on, the legacy `BlackFrameAnalyzer` replaces the keyframe analyzer; it never writes the visuals row, so it has no card run.
- **Season actions.** A BlackFrame action restricts the pass to the black-frame producer (the keyframe analyzer, or the legacy analyzer under its toggle) and keeps all of its candidates, and an available Chromaprint action restricts it to Chromaprint. Both bypass chapter authority. Chapter and unavailable Chromaprint actions follow the default policy, enhancement included. `PreferChromaprint` has no effect on credits.
- **Offsets.** Chapter authority is decided before playback adjustments. When configured offsets consume a recognized chapter's whole range, its automatic credits are cleared and the episode completes without a skip segment; combined detection does not pick another range instead.
- **Comparison pool.** Episodes settled by chapters, consumed by offsets or failed still serve as Chromaprint references for their siblings, and their own results are not replaced. Their audio is still valid, and dropping one could leave a two-episode season with nothing to compare against. A season settled entirely by chapters needs no fingerprints.
- **Settled siblings.** An already-analyzed episode is reconsidered only when a new Chromaprint candidate reaches outside its stored credits, allowing for the time adjustment, and never when a chapter is authoritative. Its chapters are read only when such a candidate could change its stored result.
- **Failures.** An episode whose fingerprint failed is settled by the other analyzers' result. With no result at all it stays failed and is retried on the next scan rather than recorded as having no credits. Chapter lookup and persistence failures are retried the same way.
- **Writes.** Each stored segment goes through the time adjustment once, the legacy analyzer's result included, and each episode is written once.

### Combining candidates

`CreditsCandidateCombiner.Combine` turns one episode's candidates into the segments to store:

- A candidate that ends within the minimum credits duration of the window end is extended to it. Nothing that short after credits can be a credits scene of its own, and shared audio stops a few seconds early at the fade-out.
- Candidates that overlap or lie within `CreditSceneBuilder.MaximumSceneMergeGapSeconds` of each other merge into one `Combined` segment from the earliest start to the latest end. Candidates farther apart are stored as separate Outro segments under their own source, so the scene between them plays.
- The end of a chapter credits candidate is an authored credits-to-content boundary, such as the start of a named preview chapter. Nothing extends or merges across it, and black-frame or Chromaprint candidates reaching past it are capped there. Later chapter markers carry no such meaning.

Order carries no information: shared audio before, inside or after a black roll merges the same way. A union only grows, so a chapter range is never shortened.

### Derived previews

When previews derive from the credits end (see `docs/analysis.md`), `AnimePreviewDeriver` starts the preview at the end of the first credits run, counting rows that overlap or touch as one whatever their source. It ends where the next run starts or at the end of the episode, and only when at least `MinimumPreviewDuration` fits. It derives right after a credits result lands, and again in the Preview mode for episodes no preview analyzer settled, so a season whose credits are all user-provided still refreshes its previews when the preview settings change. Where credits split around a mid-credits scene, that scene becomes the preview.

## The keyframe analyzer

`KeyframeAnalyzer` detects roll credits from black-frame evidence and card credits from keyframe visuals, over one keyframe scan of the whole credits window. Black credits can sit before, between or after the other candidates, so no positional shortcut is safe; the scan is cached instead.

### Pages

`IFFmpegService.ScanKeyframesAsync` decodes the keyframes from the credits start to the end of the file. The blackframe filter reports each keyframe's black percentage, and inside the credits window `signalstats` reports its luma percentiles and saturation. The two outputs are cached as two rows and joined once, by `KeyframeJoin.Pages`, into one page per keyframe. A page's time is its black-frame time, never rounded, because the lead-in, interval and boundary probes key their cache rows from it. A page has no visual past the credits window, when no visual lies within `KeyframeJoin.Tolerance`, or when ffmpeg lacks `signalstats`; every visual rule below is then inert.

The join matches times, not the frame counter both filters print. The counter is exact within one decode, but the two rows can come from separate decodes, since a visuals row can be decoded again after the black-frame row, and the times agree closely enough: to under a microsecond from FFmpeg 7.0 on.

### Facts about one keyframe

`KeyframeVisualTraits` holds what a visual shows on its own:

- **Tinted.** The 10th percentile saturation reaches `BlackSaturationMaximum`: a dark scene with a coloured background, such as a blue night cave, not a roll. It reads the background's saturation rather than the frame's mean, so coloured lettering on black stays black.
- **Lettered.** Something at least `TextContrastMinimum` luma levels above the darkest tenth, at any density and colour. A blank page is not lettered.
- **Card-like.** A dominant background within `BackgroundSpreadMaximum` levels between the 10th and 90th percentile, something at least `TextContrastMinimum` away from it, and low saturation. Text on a black, white, grey or muted card looks like that; busy content does not.
- **Solid white.** Every percentile at limited-range white. It is neither lettered nor card-like, and always content.

Black is not a trait. It is the black-frame percentage against a threshold normalized over the scan, in which tinted keyframes count as zero, so the order is forced: tinted first, then the thresholds, then black.

### Scene rules

The analyzer applies these in order. Each step's exact rule is in the named method's XML doc.

1. **Tinted keyframes are not black** (`WithoutTintedKeyframes`): they count as zero when the thresholds are normalized over the scan.
2. **Scenes.** `CreditSceneBuilder` builds scenes from runs of black keyframes, with density gating and merging. For a sparse candidate, a targeted blackdetect scan can confirm it: all the runs one blackdetect interval supports become one scene anchored to the interval start (`DetectIntervalSupportedCreditScenes`), since blackdetect found the interval black throughout.
3. **Lead-in.** A scene starts after its dark grey lead-in, the leading black keyframes that show dim content rather than black (`StartAfterDarkGreyLeadIn`). The level is the median darkest tenth of the scene's black keyframes, never below `KeyframeAnalyzer.LimitedRangeBlack`. A dim last shot before the cut to the roll is a lead-in, and so is the lighter prefix of a roll authored at two black levels; nothing the scan keeps tells them apart, and the later start skips less story. Only leading keyframes are trimmed, so a lighter section later in a scene stays. A scene dim on every black keyframe is dark story and is rejected whole.
4. **Lead-in probe.** With boundary refinement on, `LeadInProbe` decodes the frames between the last lighter keyframe and the first at the level, `LeadInProbe.Width` pixels wide, and moves the start back over frames that match the level keyframe within `KeyframeAnalyzer.BlackLevelTolerance`. The probe never keeps the lead-in: a channel logo or a burned-in subtitle reads like a credit page carried across the cut, and keeping it would restore minutes of dark story on letterboxed episodes. The result is cached under the two keyframes. A failed decode is not cached, and the scene then starts at the keyframe.
5. **Lettering gate.** A black scene lettered on no more than half of its pages that have a visual is a gap between acts, such as a cut to a commercial break, not credits. A scene with no visual on any page gets no verdict.
6. **Candidates.** Every verified scene whose refined range meets the minimum duration is a black-frame candidate, so credits split by a mid-credits scene come out as separate parts. Every scene after the last verified one counts too: the scan keeps visuals only inside the window, so a roll past a window that ends early has no verdict. An unverified scene before a verified one is never a candidate. With no verified scene, as on an ffmpeg without `signalstats`, only the latest scene that meets the minimum is a candidate, so a dark story scene earlier in the window cannot come back as credits. Boundary refinement probes a scene's keyframe gap (`RefineBoundaryAsync`) only when that could change whether the scene reaches the minimum.

A known limit: a text epilogue on black, long enough to make its own scene, is lettered like credit cards and is skipped as credits. Nothing in the keyframe statistics tells the two apart.

### Card kinds and the card run

After probing, `StampCardKinds` gives each page that has a visual its card kind, from the scenes the black-frame rules accepted and rejected. The rules apply in order:

1. A solid white page, or a tinted page that is black by the raw percentage, is content.
2. A page that is black by the raw percentage and in a rejected range is content.
3. When no scene was accepted, a card-like page is a card and any other page is content.
4. A black or card-like page in an accepted scene is a black card.
5. A card-like page that is not black is a card.
6. Any other page is content.

The kinds come after probing because only the per-keyframe traits are facts about one page. Whether a page is a lead-in, part of a gap or a black card depends on its scene, and the same lifted-black page is a lead-in at a scene's head and a roll page at its tail.

`CardRunFinder.FindCreditRange` reads only each page's time and kind and returns the latest sustained run that meets the minimum duration. A black card extends a run and counts toward its duration, but never toward its density, and the trim cadence reads black cards only when a run has fewer than two cards. A roll on its own is therefore left to the black-frame candidate, and cannot carry stray flat shots before it into the credits or trim sparse white cards next to it. The card run is a candidate like any other, not a fallback for when no black scene is found.

## Caching and hashes

- **Scan rows.** The keyframe scan keeps two rows, invalidated separately: the black-frame row by its threshold and the visuals row by its own token in `ConfigHasher`. A single joined row would rerun the whole scan to the end of the file on every visuals-format change, instead of only the visuals to the window end.
- **Lead-in rows.** `CacheEntryType.LeadIn` rows are keyed by the two keyframes that bound the probe window. The probe's width, tolerance and rule are constants, so changing any of them means bumping that row's token in `ConfigHasher`.
- **Analysis hash.** The credits analysis hash keeps its version across detection changes, so an upgrade does not force a library-wide rescan. Stored credits keep their boundaries until a rescan or normal invalidation, so upgraded Combined rows and newly analyzed Chapter rows can coexist in a season until then. Enabling `EnhanceChapterCredits` adds a credits-only token, which invalidates stored credits analysis without discarding reusable detection caches. Seasons under a BlackFrame action never consult chapters, so they omit the token and are not rescanned. Enhancement also blocks adoption of 10.11 credits records, so opting in runs combined detection.
- **Explicit rescans.** Scan Season and Scan Movie in the dashboard's Manage panel erase every timestamp for the selection, manual edits included, and its cached fingerprints before scanning. They are not a credits-only refresh.

## Judging a change

Synthetic fixtures in `IntroSkipper.Tests` pin each rule, but they cannot show what a change does to real files. Judge every change to the keyframe credits path with `tools/CreditsRunner`: run the analyzer over a labeled set of real files before and after the change, score both, and diff them. The runner's README has the commands, the label format and the plugin seams it builds on. `diff` exits with 1 on a regression and lists every file whose candidates changed, so each change is accepted by hand.
