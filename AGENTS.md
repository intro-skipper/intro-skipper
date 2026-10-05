# AGENTS.md

intro-skipper: a Jellyfin plugin (C#, `net10.0`, Jellyfin 12 packages) that detects intro and credit scenes and mirrors them into Jellyfin's MediaSegments. It loads inside the Jellyfin server process; `PluginServiceRegistrator` wires everything into the server's DI container.

## Project map

- `IntroSkipper/` plugin project
  - `Analyzers/` ChromaprintAnalyzer (cross-episode audio fingerprints), ChapterAnalyzer, BlackFrameAnalyzer (legacy, behind `UseLegacyBlackFrameAnalyzer`), Credits/KeyframeAnalyzer (black roll and card credits from one keyframe scan), AnimePreviewDeriver
  - `ScheduledTasks/` DetectSegmentsTask, BaseItemAnalyzerTask (runs analyzers per mode)
  - `Manager/` SeasonResolver (library items into seasons, one series at a time; the season key function) and QueueVerifier (analysis state per episode), MediaSegmentMirror
  - `FFmpeg/` FFmpegService (composes FFmpegProcessRunner and FFmpegVersionGate), the only media probing path
  - `Db/` database facades, entities, admission policy, legacy importer
  - `Migrations/` EF migrations for `introskipper-v2.db`
  - `SegmentChanges/` SegmentChange coordinator, intents
  - `Providers/` SegmentDtoFactory, SegmentProvider (pull path)
  - `Controllers/` plural segments API, editor contract, dashboard endpoints
  - `Data/` TickConversions, DTOs
  - `Services/` AnalysisScheduler (the analysis queue: one worker runs every pass; feeders enqueue and get a completion handle), Entrypoint (library watcher)
  - `Configuration/`, `Helper/`, `Filters/`
- `IntroSkipper.Tests/` xUnit tests
- `web/` frontend (pnpm)
- `tools/CreditsRunner/` runs the keyframe credits analyzer over real files outside Jellyfin and scores it against labels
- `CONTEXT.md` domain glossary. Use its terms in code, tests and prose.
- `docs/` `segments.md` (storage, writes, projection to Jellyfin), `analysis.md` (queue, seasons, analysis records, detection cache), `credits.md` (credits pass, keyframe analyzer)

<important if="you need to build, run tests, or work on the frontend">

Run .NET commands from the repo root. Ignore `IntroSkipper/IntroSkipper.sln`; it only contains the plugin project.

| Command | What it does |
|---|---|
| `dotnet build IntroSkipper.sln` | plugin, tests and `tools/CreditsRunner` |
| `dotnet build IntroSkipper.sln -p:SkipWebBuild=true` | skip the pnpm frontend build |
| `dotnet test` | all tests (xUnit) |
| `dotnet test --filter "FullyQualifiedName~TestSegmentTombstones"` | one class; append `.MethodName` for one test |
| `pnpm build` (in `web/`) | frontend build (there is no dev server; the dashboard only runs inside Jellyfin) |

- The plugin csproj's `WebBuild` target runs `pnpm install --frozen-lockfile && pnpm build` in `web/` before every compile. Pass `-p:SkipWebBuild=true` when only touching C#.
- `TreatWarningsAsErrors` with StyleCop and all analyzers enabled (`AnalysisMode=AllEnabledByDefault`). Any new warning fails the build.
- Tests need jellyfin-ffmpeg with chromaprint. On Windows, `WindowsFfmpegTestBootstrap` downloads a portable build on first run and extracts `ffmpeg.exe` and `ffprobe.exe` to `IntroSkipper.Tests/bin/Debug/net10.0/_ffmpeg/extract/`; use those for experiments. Linux/CI installs the `jellyfin-ffmpeg7` package.
- Tests and CI pin jellyfin-ffmpeg 7.1.3, but Jellyfin 12 servers run 8.x. Check a change to ffmpeg arguments or output parsing on both.
- `web/` is plain TypeScript plus Vite, no framework. `pnpm build` runs `tsc` first, so a type error in `web/` fails the plugin build unless you pass `SkipWebBuild`.
</important>

<important if="you are writing or changing tests">
Tautological tests considered harmful.

Reuse the shared seams in `IntroSkipper.Tests/` instead of building new ones:
- `TempSegmentDb`, `TempCacheDb` (temp-file segment and detection-cache databases, deleted on dispose), `DatabaseTestHelpers` (facades over temp-file SQLite), `TempJellyfinDb` (real `JellyfinDbContext` over temp SQLite)
- `SegmentChangeHarness` (production segment-change composition over a temp DB and a fake store), `SegmentSeeding` (seeds user state through `ApplyChangeAsync`), `EntrypointTestHelpers` (entrypoint over fresh databases)
- `FakeJellyfinSegmentStore` (records calls, can throw or park), `FakeMirrorPolicy` (settable policy with a toggle event), `StubFFmpegService` (counting `IFFmpegService` with per-member hooks), `ChapterManagerStub` (fixed chapter list), `JellyfinItems` (library items shaped for the season resolver, `WithParents` adds the series and seasons the episodes name)
- `FfmpegTestHelpers` (real ffmpeg over the fixture media), `LegacySchemaFixtures` (pre-v2 rows for importer tests)

Test parallelization is disabled assembly-wide, so tests may share `Plugin.Instance`.
</important>

<important if="you are creating a branch, picking a PR base, or rebasing">
There is no `main`/`master`. Branches are named for Jellyfin releases, and origin's default, `12.0`, is the current line. Target the release branch your feature branch was cut from (check the merge base).
</important>

<important if="you are editing IntroSkipper.csproj or package references">
Never add a ProjectReference to a local `../../jellyfin` checkout. It causes NU1605 restore errors and breaks the plugin's assembly load context.
</important>

<important if="you are working on analyzers, the detection pipeline, or scheduled tasks">
`DetectSegmentsTask`, the watcher (`Entrypoint`) and the manual scan endpoint enqueue into `AnalysisScheduler`, the only thing that runs `BaseItemAnalyzerTask`, which resolves seasons through `SeasonResolver` (per series, Jellyfin's own season rule, in-season specials with their host season) and orchestrates the analyzers per mode: a first-wins chain for every mode except credits, which `CreditsPass` (in `Analyzers/Credits/`) analyzes by combining every analyzer's candidate per episode. The pipeline is in `docs/analysis.md` and the credits rules in `docs/credits.md`. All media probing goes through `FFmpegService`. Raw detection output is cached in the detection-cache DB keyed by config hash. A setting that changes detection output must be added to `Helper/ConfigHasher`, or cached results are reused under the old settings.

Judge every keyframe credits change with `tools/CreditsRunner` against a baseline run; its README has the commands.
</important>

<important if="you are touching databases, EF Core, migrations, or anything in Db/">
Two plugin-owned SQLite databases sit behind singleton facades (`IntroSkipperDatabase`, `DetectionCacheDatabase`) that own initialization and migrations via internal retryable gates. Consumers go through a facade, never a DbContext directly. `introskipper-v2.db` is the source of truth for segments. The legacy `introskipper.db` is imported exactly once, read-only, by `LegacyDatabaseImporter`. Design rationale (plural segments, tombstones, provenance, shared Guid v7 ids): `docs/segments.md`. Analysis records and the detection cache: `docs/analysis.md`.
</important>

<important if="you are changing how segments reach Jellyfin: the mirror, projection worker, SegmentProvider, or MediaSegments">
Jellyfin's MediaSegments table is a per-item mirror of the plugin DB. `SegmentDtoFactory` is the single conversion source for both the push path (`MediaSegmentMirror` to `JellyfinSegmentStore`, the only write path into the mirror, driven by the projection worker under its per-item lock) and the pull path (`SegmentProvider`), so Jellyfin-initiated runs converge to the same data.
</important>

<important if="you are adding or changing any write to segment data">
`SegmentChange` (hosted coordinator, injected as the concrete class) commits typed intents through `IntroSkipperDatabase.ApplyChangeAsync`: mutation, analysis bookkeeping, and projection journal in one transaction. It then projects by re-syncing current truth through the mirror. The `ProjectionQueue` journal plus durable foreign-row deletes retries failures with backoff, recovers at startup, and replays when mirroring is re-enabled. Analyzer and maintenance facade writes that change servable state journal their items' markers in their own transactions. The journal stores work, not images; `docs/segments.md` explains why.
</important>

<important if="you are adding or changing controllers or HTTP endpoints">
Every interactive mutation (plural segments API, `MediaSegmentsApi` editor contract, per-item disable) commits through `SegmentChange.ApplyAsync`. Controllers only validate and map typed outcomes to HTTP via `SegmentChangeHttp`: applied changes keep their established shapes, pending or skipped projections answer 202 with `SegmentChangeAcceptedResponse`, rejections map to 404/400.
</important>

<important if="you are handling segment times, tombstones, automatic segment writes, or disabled items">
- Ticks internally, seconds only at the analyzer and HTTP edges (`Data/TickConversions`).
- Deleting an automatic segment leaves a tombstone that blocks automatic re-insertion. `AutoSegmentAdmissionPolicy` gates automatic writes so automation never contradicts recorded human intent.
- Per-item disable (`DisabledItems`) filters only the servable read. Analysis and storage are unaffected.
</important>

<important if="you are adding error handling, argument guards, or try/catch">
Internal code trusts its callers. Validation happens at the edges: controllers, configuration, and ffmpeg output. Catch blocks belong to the layers that own recovery (the ffmpeg process runner, the database gates, the projection journal, the analysis pass). A new one anywhere else needs a reason in the diff.
</important>

<important if="you are changing behavior that `docs/segments.md`, `docs/analysis.md` or `docs/credits.md` describes">
Update the doc in the same diff. These docs describe current behavior, so a sentence the diff makes false misleads every later reader.
</important>

<important if="you are writing XML docs or code comments">
Document purpose, invariants, errors, side effects, async or blocking behavior, and examples for non-obvious APIs. Skip comments that only restate names or obvious control flow.
</important>

<important if="you are running gh or working with GitHub issues or triage labels">
Issues live on `intro-skipper/intro-skipper`, which is origin, so a bare `gh` command resolves correctly. The `rlauuzo/*` forks have issues disabled. Triage labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`.
</important>
