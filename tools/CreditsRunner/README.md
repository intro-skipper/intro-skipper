# CreditsRunner

Runs the keyframe credits analyzer over real media files outside Jellyfin, scores the result against labeled credits, and diffs two runs. Every change to the keyframe credits path is judged this way against a baseline run.

```sh
dotnet run --project tools/CreditsRunner -- run --manifest corpus.json --out results.json
dotnet run --project tools/CreditsRunner -- score --results results.json --labels labels.json
dotnet run --project tools/CreditsRunner -- diff --baseline before.json --candidate after.json --labels labels.json
```

`run` takes these options:

- `--repeat 3` times three passes per file after an untimed pass that warms the page cache. Every pass starts with an empty detection cache.
- `--minimum-credits-duration 10` overrides that setting. Every other setting is the plugin default.
- `--ffmpeg /usr/lib/jellyfin-ffmpeg` puts that directory first on PATH.

For the timing box, publish self-contained with `dotnet publish tools/CreditsRunner -c Release -r linux-x64 --self-contained -p:SkipWebBuild=true`.

`diff` exits with 1 when it finds a regression. Per file, a regression is a new failure, a new false part, or more than 0.5 s of story the baseline did not skip. Per labeled part, it is a new miss, or a start or end error that grew by more than 0.5 s. `diff` also lists every file whose candidates changed, so each change can be accepted by hand, and warns when the two runs differ in configuration, ffmpeg build, CPU or repeats.

`run` refuses a manifest entry whose file is missing or whose duration is not positive, and every command rejects an option it does not take, so a typo cannot quietly change what is measured.

## Files

The manifest holds local paths and stays private. Paths are absolute or relative to the manifest. `duration` is Jellyfin's Duration for the item, because the credits window derives from it.

```json
{ "files": [{ "id": "Show S01E02", "path": "tv/Show/S01E02.mkv", "duration": 2702.4, "movie": false }] }
```

The labels file holds no paths. Each file lists its roll and card credits parts in order, and a file without credits has an empty list. A boundary is a time, or a band `[earliest, latest]` for a fade or a cut to black, and any point inside a band scores zero. `ignore` holds credits out of scope, such as styled credits or credits over footage, which are never required and never penalized. A file with `holdout` set is reported apart, and no prototype tunes on it. `kind` and `traps` are notes for people; the scorer skips them.

```json
{ "files": [{
  "id": "Show S01E02", "holdout": false,
  "parts": [{ "start": [2610.0, 2611.2], "end": 2702.4, "kind": "roll" }],
  "ignore": [{ "start": 2580.0, "end": 2610.0 }],
  "traps": [{ "start": 2550.0, "end": 2580.0, "note": "dim scene" }]
}] }
```

The scorer judges the candidates after `CreditsCandidateCombiner.Combine`, which is what the keyframe analyzer contributes to a stored row. The raw candidates are kept for diagnosis. It matches each labeled part to at most one prediction by overlap. A prediction that matches no part is false when it skips more than 0.5 s of story. It reports signed and absolute start and end errors, story seconds skipped, credits seconds missed, missed and false parts, and hit rates at 0.5, 1, 2 and 5 s.

The results record the commit the plugin was built from, the ffmpeg version, the CPU, the configuration and, for each file, every call the analyzer made into `IFFmpegService`. Each call has the ffmpeg processes it started, its wall-clock and, on Linux, their CPU seconds from `getrusage(RUSAGE_CHILDREN)`.

## Seams

The runner builds these plugin pieces directly, through `InternalsVisibleTo`. A rewrite that changes one of them changes the runner with it:

- `KeyframeAnalyzer`, its constructor and `DetectCreditsAsync(QueuedEpisode, CancellationToken)`
- `CreditsCandidateCombiner.Combine`
- `FFmpegService(ILogger<FFmpegService>, DetectionCacheService)`, plus the debug line `Starting ffmpeg with the following arguments` that `FFmpegProcessRunner` logs before each start, which the runner counts
- `FFmpegService.CheckFFmpegVersionAsync` and `GetCheckResult()`, run before each pass as the analysis pass runs them, since the check decides whether the scan reads keyframe visuals; the results take the ffmpeg version from its output named `version`
- `DetectionCacheService`, `DetectionCacheDatabase`, `DetectionCacheDbContext` and `SqlitePragmas.Configure`
- `AttributedSegment` and `SegmentSource`, the candidates' shape
- `QueuedEpisode`, with the credits window set as `BaseItemAnalyzerTask.SetCreditsWindowsAsync` sets it when ProbeAudioDuration is off
- `PluginConfiguration`

`IFFmpegService` sits behind a `DispatchProxy` that times every method returning a task, so new or renamed service calls need no change here.
