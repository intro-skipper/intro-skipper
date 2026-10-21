# CreditsRunner

Runs the keyframe credits analyzer, or in intro mode the Introduction chain, over real media files outside Jellyfin, scores the result against labeled parts, and diffs two runs. Every change to the keyframe credits path, and every Chapter, Chromaprint or time adjustment change to Introduction, is judged this way against a baseline run.

```sh
dotnet run --project tools/CreditsRunner -- run --manifest corpus.json --out results.json
dotnet run --project tools/CreditsRunner -- run --mode intro --manifest intros.json --out results.json
dotnet run --project tools/CreditsRunner -- score --results results.json --labels labels.json
dotnet run --project tools/CreditsRunner -- diff --baseline before.json --candidate after.json --labels labels.json
```

`run` takes these options:

- `--mode intro` runs the Introduction chain instead of the credits path. `--mode credits` is the default.
- `--no-chapters`, intro mode only, drops every chapter after reading it, so no file is settled by a chapter before Chromaprint and the time adjustment has no chapter to snap to.
- `--repeat 3` times three passes per file after an untimed pass that warms the page cache. Every pass starts with an empty detection cache. Credits mode only.
- `--minimum-credits-duration 10` overrides that setting. Every other setting is the plugin default. Credits mode only.
- `--ffmpeg /usr/lib/jellyfin-ffmpeg` puts that directory first on PATH.

For the timing box, publish self-contained with `dotnet publish tools/CreditsRunner -c Release -r linux-x64 --self-contained -p:SkipWebBuild=true`.

`diff` exits with 1 when it finds a regression. Per file, a regression is a new failure, a new false part, or more than 0.5 s of story the baseline did not skip. Per labeled part, it is a new miss, or a start or end error that grew by more than 0.5 s. `diff` also lists every file whose candidates changed, so each change can be accepted by hand, and warns when the two runs differ in configuration, ffmpeg build, CPU, repeats or `--no-chapters`. It refuses two runs of different modes. A regression on a held-out file is tagged `(holdout)` and counted apart from tuning regressions.

`run` refuses a manifest entry whose file is missing, whose duration is not positive or whose id repeats, an `--out` that names the manifest or a corpus file, and, in intro mode, a movie, since the analysis pass never fingerprints one. Every command rejects an option it does not take, one given twice, or one given without its value, so a typo cannot quietly change what is measured.

## Intro mode

Intro mode groups the files by `season` and runs each season as the analysis pass runs Introduction at default settings (`BaseItemAnalyzerTask.RunAnalyzerChainAsync`): it reads each file's embedded chapters, runs the chapter analyzer, then Chromaprint over the files the chapter analyzer left open. Both write to a temp segment database, and a file's candidates are its Introduction rows after the time adjustment, with their source, `Chapter` or `Chromaprint`. A file whose chapters ffprobe cannot read sits out and fails with `chapters unreadable`, and a file the chain marks failed fails with `analysis failed`. An exception out of the chain fails the files still open with it and keeps the rows the analyzers committed before it, as the analysis pass does. A failed file has no candidates. Every season starts with an empty detection cache and segment database.

It does not emulate the JellyfinTable chapter source, a per-season `AnalyzerAction`, `PreferChromaprint`, settled-season reanalysis, or a later pass that compares a new episode with settled siblings. For a file no other plugin touched, the embedded chapters equal Jellyfin's table, except where `docs/analysis.md` says the two sources differ: a chapter with a negative start, Jellyfin's dummy chapters, untitled chapters named under another server language, or a table a Jellyfin before 10.11 filled. The run records `UseEmbeddedChaptersForAnalysis` as on.

## Files

The manifest holds local paths and stays private. Paths are absolute or relative to the manifest. `duration` is Jellyfin's Duration for the item, because the credits and intro windows derive from it. Files with the same `season` form one season in intro mode, in manifest order, which sets each file's episode number. A file without `season` is a season of its own, which Chromaprint skips: it compares two files or more. Credits mode ignores `season`.

```json
{ "files": [{ "id": "Show S01E02", "path": "tv/Show/S01E02.mkv", "duration": 2702.4, "movie": false, "season": "Show S01" }] }
```

The labels file holds no paths. Each file lists its labeled parts in order, roll and card credits in credits mode or the intro in intro mode, and a file without one has an empty list. Intro labels go in their own file. A boundary is a time, or a band `[earliest, latest]` for a fade or a cut to black, and any point inside a band scores zero. `ignore` holds parts out of scope, such as styled credits or credits over footage, which are never required and never penalized. A file with `holdout` set is reported apart, and no prototype tunes on it. `kind` and `traps` are notes for people; the scorer skips them.

```json
{ "files": [{
  "id": "Show S01E02", "holdout": false,
  "parts": [{ "start": [2610.0, 2611.2], "end": 2702.4, "kind": "roll" }],
  "ignore": [{ "start": 2580.0, "end": 2610.0 }],
  "traps": [{ "start": 2550.0, "end": 2580.0, "note": "dim scene" }]
}] }
```

In credits mode the scorer judges the candidates after `CreditsCandidateCombiner.Combine`, which is what the keyframe analyzer contributes to a stored row. The raw candidates are kept for diagnosis. Intro mode keeps no raw candidates. The scorer matches each labeled part to at most one prediction by overlap. A prediction that matches no part is false when it skips more than 0.5 s of story. It reports signed and absolute start and end errors, story seconds skipped, credits or intro seconds missed, missed and false parts, and hit rates at 0.5, 1, 2 and 5 s.

The results record the mode, whether `--no-chapters` was set, the commit the plugin was built from, the ffmpeg version, the CPU, the configuration and, for each file, every call the analyzer made into `IFFmpegService`. Each call has the ffmpeg processes it started, its wall-clock and, on Linux, their CPU seconds from `getrusage(RUSAGE_CHILDREN)`. Results written before intro mode existed read as credits mode with chapters.

In intro mode a call belongs to the file its `QueuedEpisode` argument names. A file's `wallSeconds` and `cpuSeconds` sum its calls, and `cpuSeconds` is null when any of them has none. `pluginCpuSeconds` is 0 and `nondeterministic` false, since a season pass can attribute neither to one file. Judge intro cost by the call counts per trigger that `score` prints.

## Seams

The runner builds these plugin pieces directly, through `InternalsVisibleTo`. A rewrite that changes one of them changes the runner with it:

- `KeyframeAnalyzer`, its constructor and `DetectCreditsAsync(QueuedEpisode, CancellationToken)`
- `CreditsCandidateCombiner.Combine`
- `FFmpegService(ILogger<FFmpegService>, DetectionCacheService)`, plus the debug line `Starting ffmpeg with the following arguments` that `FFmpegProcessRunner` logs before each start, which the runner counts
- `FFmpegService.CheckFFmpegVersionAsync` and `GetCheckResult()`, run before each pass as the analysis pass runs them, since the check decides whether the scan reads keyframe visuals and whether intro mode runs Chromaprint; the results take the ffmpeg version from its output named `version`
- `DetectionCacheService`, `DetectionCacheDatabase`, `DetectionCacheDbContext` and `SqlitePragmas.Configure`
- `AttributedSegment` and `SegmentSource`, the candidates' shape
- `QueuedEpisode`, with the credits window set as `BaseItemAnalyzerTask.SetCreditsWindowsAsync` sets it when ProbeAudioDuration is off. The keyframe analyzer reads no chapters, so credits mode leaves `QueuedEpisode.Chapters` empty where the season pass would fill it. Intro mode sets `IntroFingerprintEnd` as `SeasonResolver` does and `EpisodeNumber` from the manifest order, which Chromaprint reads to pad a lone open episode with its neighbours
- `PluginConfiguration`
- intro mode: `ChapterAnalyzer` and `ChromaprintAnalyzer`, their constructors and `AnalyzeMediaFiles`, in the order `BaseItemAnalyzerTask.RunAnalyzerChainAsync` runs them for Introduction at default settings. A change to that chain changes the runner with it
- intro mode: `IntroSkipperDatabase(IDbContextFactory<IntroSkipperDbContext>, ILogger)` and `GetSegmentsAsync`, `IFFmpegService.GetEmbeddedChaptersAsync`, and `ConfigHasher.Analysis`, which stamps the temp rows

`IFFmpegService` sits behind a `DispatchProxy` that times every method returning a task, so new or renamed service calls need no change here.

Outside Jellyfin, `Plugin.Instance` is null, so the settings `FFmpegService` and `DetectionCacheService` read from it take their defaults whatever the run's configuration says: `SilenceDetectionMaximumNoise`, `PreferredAudioLanguage`, `PreferAudioStreamWithMostChannels`, `ProcessThreads` and `ScanTimeoutSeconds`, and the configuration the detection cache hashes. The embedded chapter read names untitled chapters `Chapter {0}`, the format of an English-language server.
