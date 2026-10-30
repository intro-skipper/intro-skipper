# CreditsRunner

Runs the keyframe credits analyzer, or one season at a time the Introduction chain or the whole credits pass, over real media files outside Jellyfin, scores the result against labeled parts, and diffs two runs. Every change to the keyframe credits path, and every Chapter, Chromaprint or time adjustment change to Introduction, is judged this way against a baseline run.

```sh
dotnet run --project tools/CreditsRunner -- run --manifest corpus.json --out results.json
dotnet run --project tools/CreditsRunner -- run --mode intro --manifest intros.json --out results.json
dotnet run --project tools/CreditsRunner -- run --mode credits-pass --manifest seasons.json --out results.json
dotnet run --project tools/CreditsRunner -- score --results results.json --labels labels.json
dotnet run --project tools/CreditsRunner -- diff --baseline before.json --candidate after.json --labels labels.json
```

`run` takes these options:

- `--mode credits`, the default, runs the keyframe credits path one file at a time. `--mode intro` runs the Introduction chain, and `--mode credits-pass` the whole credits pass (`CreditsPass`: chapters, Chromaprint and the keyframe analyzer), one season at a time. See Season runs.
- `--no-chapters`, intro and credits-pass modes only, drops every chapter after reading it, so no file is settled by a chapter before Chromaprint and the time adjustment has no chapter to snap to.
- `--repeat 3` times three passes per file after an untimed pass that warms the page cache. Every pass starts with an empty detection cache. Credits mode only.
- `--minimum-credits-duration 10` overrides that setting. Every other setting is the plugin default. Credits and credits-pass modes only.
- `--ffmpeg /usr/lib/jellyfin-ffmpeg` puts that directory first on PATH.

For the timing box, publish self-contained with `dotnet publish tools/CreditsRunner -c Release -r linux-x64 --self-contained -p:SkipWebBuild=true`.

`diff` exits with 1 when it finds a regression. Per file, a regression is a new failure, a new false part, or more than 0.5 s of story the baseline did not skip. Per labeled part, it is a new miss, or a start or end error that grew by more than 0.5 s. `diff` also lists every file whose candidates changed, so each change can be accepted by hand, and warns when the two runs differ in configuration, ffmpeg build, CPU, repeats or `--no-chapters`. It refuses two runs of different modes. A regression on a held-out file is tagged `(holdout)` and counted apart from tuning regressions. When either run has an ffmpeg process killed at its timeout, `diff` warns for each such file, keeps the full output, and exits with 1: the analyzer falls back to another boundary when a probe times out, so machine load can move a boundary with no code change. In a season run the warning names only the file whose call timed out, but Chromaprint compares each file with its season, so a regression on another file of that season may come from the same timeout.

`run` refuses a manifest entry whose file is missing, whose duration is not positive or whose id repeats, an `--out` that names the manifest or a corpus file, and, in a season run, a movie, since the analysis pass never fingerprints one. Every command rejects an option it does not take, one given twice, or one given without its value, so a typo cannot quietly change what is measured.

## Season runs

Intro and credits-pass modes group the files by `season`. Intro mode runs each season as the analysis pass runs Introduction at default settings (`BaseItemAnalyzerTask.RunAnalyzerChainAsync`): it reads each file's embedded chapters, runs the chapter analyzer, then Chromaprint over the files the chapter analyzer left open. Both write to a temp segment database, and a file's candidates are its Introduction rows after the time adjustment, with their source, `Chapter` or `Chromaprint`. A file whose chapters ffprobe cannot read sits out and fails with `chapters unreadable`, and a file the chain marks failed fails with `analysis failed`. An exception out of the chain keeps the rows the analyzers committed before it, as the analysis pass does, and fails the files still open, since the run is the only pass. A failed file has no candidates. Every season starts with an empty detection cache and segment database.

Credits-pass mode runs each season through `CreditsPass` as `BaseItemAnalyzerTask` runs it at default settings, with the credits window `SetCreditsWindowsAsync` sets, and a file's candidates are its stored Credits rows after the time adjustment, usually with the source `Combined`. Season runs keep no raw candidates, and the scorer judges the stored rows. Chromaprint compares each file with the rest of its season, so one file without `season` gets the keyframe and chapter candidates only.

Neither emulates the JellyfinTable chapter source, a per-season `AnalyzerAction`, `PreferChromaprint`, settled-season reanalysis, or a later pass that compares a new episode with settled siblings. Credits-pass mode writes no Introduction rows, so it lacks the admission rule that refuses a Credits row overlapping an active intro, which matters only where the credits window reaches the intro. For a file no other plugin touched, the embedded chapters equal Jellyfin's table, except where `docs/analysis.md` says the two sources differ: a chapter with a negative start, Jellyfin's dummy chapters, untitled chapters named under another server language, or a table a Jellyfin before 10.11 filled. The run records `UseEmbeddedChaptersForAnalysis` as on.

## Files

The manifest holds local paths and stays private. Paths are absolute or relative to the manifest. `duration` is Jellyfin's Duration for the item, because the credits and intro windows derive from it. Files with the same `season` form one season in a season run, in manifest order, which sets each file's episode number. A file without `season` is a season of its own, which Chromaprint skips: it compares two files or more. The keyframe credits mode ignores `season`.

```json
{ "files": [{ "id": "Show S01E02", "path": "tv/Show/S01E02.mkv", "duration": 2702.4, "movie": false, "season": "Show S01" }] }
```

The labels file holds no paths. Each file lists its labeled parts in time order, roll and card credits in credits mode or the intro in intro mode, and a file without one has an empty list. Intro labels go in their own file. A boundary is a time, or a band `[earliest, latest]` for a fade or a cut to black, and any point inside a band scores zero. `ignore` holds parts out of scope, such as styled credits or credits over footage, which are never required and never penalized. A file with `holdout` set is reported apart, and no prototype tunes on it. `score` and `diff` refuse a labels file where a part's end band ends after the next part's start band begins, or where a part has no required credits: no time between its start band and its end band outside ignore ranges. `kind` and `traps` are notes for people, but the scorer warns for each trap that overlaps an ignore range, since ignore time never counts as story.

```json
{ "files": [{
  "id": "Show S01E02", "holdout": false,
  "parts": [{ "start": [2610.0, 2611.2], "end": 2702.4, "kind": "roll" }],
  "ignore": [{ "start": 2580.0, "end": 2610.0 }],
  "traps": [{ "start": 2550.0, "end": 2580.0, "note": "dim scene" }]
}] }
```

In credits mode the scorer judges the candidates after `CreditsCandidateCombiner.Combine`, which is what the keyframe analyzer contributes to a stored row. The raw candidates are kept for diagnosis. A season run keeps no raw candidates. The scorer matches each labeled part to at most one prediction, by overlap with the seconds the part always covers outside ignore ranges, so a prediction over only a band or ignore time matches no part. One prediction can match several parts of a block: neighbouring parts with at most 0.5 s of story between them, such as a card and a roll around ignored titles. A start or end error is the seconds the boundary costs: an early start or a late end counts the story between it and the band, and a late start or an early end counts the required credits it leaves out. Ignore ranges and other parts cost nothing, so a start anywhere in ignored credits that run into the part scores zero. Where the same prediction matches two neighbouring parts, the boundaries between them are measured to each other's bands, so story before or after the block counts once, on its first or last part, and the story between the two parts, at most 0.5 s, counts on both. A part whose neighbour is matched by another prediction or by none is measured to the prediction's own edge, so story outside the block then counts on every part that prediction matches. A prediction that matches no part is false when it skips more than 0.5 s of story. It reports signed and absolute start and end errors, story seconds skipped, credits or intro seconds missed, missed and false parts, and hit rates at 0.5, 1, 2 and 5 s.

The results record the mode, whether `--no-chapters` was set, the commit the plugin was built from, the ffmpeg version, the CPU, the configuration and, for each file, every call the analyzer made into `IFFmpegService`. Each call has the ffmpeg processes it started, its wall-clock and, on Linux, their CPU seconds from `getrusage(RUSAGE_CHILDREN)`, and `timedOut`, the processes killed at their timeout, the most of any timed pass. When the timed passes made different calls, the file keeps the calls of the pass with the most timeouts. `score` lists each call with one as `unfinished` and prints the total, which should be 0 on an idle machine. Results written before intro mode existed read as credits mode with chapters.

In a season run a call belongs to the file its `QueuedEpisode` argument names. A file's `wallSeconds` and `cpuSeconds` sum its calls, and `cpuSeconds` is null when any of them has none. `pluginCpuSeconds` is 0 and `nondeterministic` false, since a season pass can attribute neither to one file. Judge a season run's cost by the call counts per trigger that `score` prints.

## Seams

The runner builds these plugin pieces directly, through `InternalsVisibleTo`. A rewrite that changes one of them changes the runner with it:

- `KeyframeAnalyzer`, its constructor and `DetectCreditsAsync(QueuedEpisode, CancellationToken)`
- `CreditsCandidateCombiner.Combine`
- `FFmpegService(ILogger<FFmpegService>, DetectionCacheService)`, plus the debug line `Starting ffmpeg with the following arguments` that `FFmpegProcessRunner` logs before each start, which the runner counts, and the warning `ffmpeg did not exit within` it logs when it kills a process at its timeout, which the runner counts per call
- `FFmpegService.CheckFFmpegVersionAsync` and `GetCheckResult()`, run before each pass as the analysis pass runs them, since the check decides whether the scan reads keyframe visuals and whether a season run runs Chromaprint; the results take the ffmpeg version from its output named `version`. A timeout during the check stops the run with exit code 2, since no recorded call would show it
- `DetectionCacheService`, `DetectionCacheDatabase`, `DetectionCacheDbContext` and `SqlitePragmas.Configure`
- `AttributedSegment` and `SegmentSource`, the candidates' shape
- `QueuedEpisode`, with the credits window set as `BaseItemAnalyzerTask.SetCreditsWindowsAsync` sets it when ProbeAudioDuration is off. The keyframe analyzer reads no chapters, so credits mode leaves `QueuedEpisode.Chapters` empty where the season pass would fill it. A season run sets `IntroFingerprintEnd` as `SeasonResolver` does and `EpisodeNumber` from the manifest order, which Chromaprint reads to pad a lone open episode with its neighbours
- `PluginConfiguration`
- intro mode: `ChapterAnalyzer` and `ChromaprintAnalyzer`, their constructors and `AnalyzeMediaFiles`, in the order `BaseItemAnalyzerTask.RunAnalyzerChainAsync` runs them for Introduction at default settings. A change to that chain changes the runner with it
- credits-pass mode: `CreditsPass`, its constructor and `RunAsync`, with `AnalyzerAction.Default`
- season runs: `IntroSkipperDatabase(IDbContextFactory<IntroSkipperDbContext>, ILogger)` and `GetSegmentsAsync`, `IFFmpegService.GetEmbeddedChaptersAsync`, and `ConfigHasher.Analysis`, which stamps the temp rows

`IFFmpegService` sits behind a `DispatchProxy` that times every method returning a task, so new or renamed service calls need no change here.

Outside Jellyfin, `Plugin.Instance` is null, so the settings `FFmpegService` and `DetectionCacheService` read from it take their defaults whatever the run's configuration says: `SilenceDetectionMaximumNoise`, `PreferredAudioLanguage`, `PreferAudioStreamWithMostChannels`, `ProcessThreads` and `ScanTimeoutSeconds`, and the configuration the detection cache hashes. The embedded chapter read names untitled chapters `Chapter {0}`, the format of an English-language server.
