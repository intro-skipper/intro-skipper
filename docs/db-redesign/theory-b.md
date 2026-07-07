# Theory B: One Cohesive Facade Service per Database

Exploration spike for the intro-skipper (branch `12.0`) database-layer redesign.
Status: **prototype implemented, compiling, StyleCop-clean, 377/378 tests passing**
(the one failure is the pre-existing environmental `TestAudioFingerprinting.TestSilenceDetection`).

---

## 1. Architecture overview

Two sealed singleton services, one per SQLite database, each closed over an
`IDbContextFactory<TContext>`:

| Facade | Database | Context | Concerns |
|---|---|---|---|
| `IIntroSkipperDatabase` / `IntroSkipperDatabase` | `introskipper.db` | `IntroSkipperDbContext` | segments, season state, maintenance, lifecycle (legacy repair + migrations + rebuild) |
| `IDetectionCacheDatabase` / `DetectionCacheDatabase` | `introskipper-cache.db` | `DetectionCacheDbContext` | cache CRUD, stale-ID computation, lifecycle (`EnsureCreated` + corruption recovery) |

Principles:

1. **Mechanical first, modern second.** Every method body is a 1:1 move of the
   existing `Plugin` / call-site code. EF10 modernization (compiled queries,
   `EF.Parameter` collection translation) is applied *inside* the facade afterwards, one operation at a time,
   where a diff reviewer can see it against the verbatim-moved baseline.
2. **The facade is the domain boundary.** The two invariants that guard writes —
   user-provided segments are never overwritten by analysis results, and auto-detected
   credits must not overlap the stored intro — live inside
   `IntroSkipperDatabase.UpdateTimestampAsync`, exactly where they lived in `Plugin`.
   No caller can reach a `DbContext` and bypass them (in the end state; see §3).
3. **Stateless services.** Each operation creates a fresh short-lived context from the
   injected factory — identical to the current `using var db = Plugin.CreateDbContext()`
   discipline, so the concurrency model is unchanged. The only state is a one-shot
   initialization gate (§5).
4. **Lifecycle is part of the facade.** Migrations, legacy schema repair,
   `RebuildDatabaseAsync`, and the cache's delete-and-recreate recovery are facade
   members. Rationale: initialization *is* a database concern with the same
   dependencies (factory + logger), and putting it behind the same interface is what
   makes the ordering guarantee in §5 enforceable. A separate initializer class would
   reintroduce a "who runs first" coupling between two objects. A thin
   `IntroSkipperDatabaseInitializer : IHostedService` exists, but only as an eager
   warm-up; it contains no logic beyond calling `InitializeAsync()`.

### File layout and anti-god-class conventions

The implementation is one class split across partial files **by aggregate**, mirroring
how a reviewer thinks about the schema:

```
Db/
  IIntroSkipperDatabase.cs                interface, fully XML-documented (the contract page)
  IntroSkipperDatabase.cs                 ctor, init gate, rebuild, LoggerMessages
  IntroSkipperDatabase.Segments.cs        DbSegment reads/writes (+ compiled query)
  IntroSkipperDatabase.SeasonStates.cs    DbSeasonState reads/writes
  IntroSkipperDatabase.Maintenance.cs     bulk cleanup spanning both tables
  IDetectionCacheDatabase.cs
  DetectionCacheDatabase.cs               single file; the cache is 8 methods
  IntroSkipperDatabasePaths.cs            single source of truth for DB file paths
  IntroSkipperDbContextPathFactory.cs     transitional/test factory (path resolved lazily)
  DetectionCacheDbContextPathFactory.cs   transitional/test factory
```

Conventions that keep a ~23-method interface maintainable:

- **One partial file per aggregate; a method goes where its *written* table lives.**
  Read-only cross-aggregate queries (`GetSeasonQueueSnapshotAsync`) go with the
  aggregate that names them (season). Cross-aggregate *writes* go in `Maintenance`.
- **Naming:** `Get*` (read), `Set*`/`Update*`/`Record*` (upsert), `Delete*` (targeted
  delete), `Clean*` (retention sweep: "delete everything NOT in this set"),
  `Reset*`/`Clear*` (state reset), `Rebuild*` (lifecycle). The names are preserved
  from `Plugin` so `git log -S` archaeology still works.
- **No pass-through growth:** a new method must contain at least one context operation;
  orchestration that spans the two databases (e.g. "erase season then erase cache")
  stays at the call site — that is why `VisualizationController.EraseSeasonAsync`
  composes `DeleteSegmentsForItemsAsync` + cache delete + `ClearSeasonEpisodeIdsAsync`
  rather than getting a bespoke `EraseSeasonAsync` facade method.
- **Split trigger:** if either partial file's method count grows past ~15, split the
  interface along the partial-file seam (`ISegmentStore`, `ISeasonStateStore`) — the
  file layout already *is* that seam, so the refactor is rename-only. I deliberately
  did **not** pre-split: today's consumers (analyzer task, queue manager, controllers)
  each use methods from more than one group, so two interfaces would force most
  constructors to take both, which is bloat without benefit.

### Why one facade beats layered stores / no-repository here (short form)

- The domain is tiny (3 entities, 2 files) and the invariants are *write-path*
  invariants. A single home per database puts every invariant-bearing write in one
  reviewable file set with zero indirection.
- 18 scattered `Plugin` methods + 10 raw context call sites is precisely the failure
  mode of "no repository" in this codebase already — the refactor's main value is the
  *inventory*, and a facade makes the inventory the API.
- Layered stores (per-entity repositories + a coordinator) add a composition layer that
  this schema cannot pay for: 2 of the operations span both tables in one transaction
  (`ResetSeasonForReanalysisAsync`) — in a per-entity design those become either a leaky
  unit-of-work abstraction or a third "coordinator" that is a facade by another name.

---

## 2. Complete migration inventory

Every current DB method and direct-context call site → its exact new home.
"Bridge (transitional)" = the `Plugin` wrapper still exists in the prototype and
delegates to the facade; the end state deletes the wrapper and injects the facade
through the constructor chain (§3).

### 2a. `Plugin` methods → `IntroSkipperDatabase`

| Current (`Plugin`) | New home | Prototype consumer state |
|---|---|---|
| `UpdateTimestampAsync` (instance) | `IIntroSkipperDatabase.UpdateTimestampAsync` | `SkipIntroController` injected; analyzers/`SegmentEditorController`/`BaseItemAnalyzerTask` via bridge |
| `GetTimestampsAsync` | `IIntroSkipperDatabase.GetTimestampsAsync` | `SkipIntroController` injected; `RecapDetectionHelper` via bridge |
| `GetSegmentsAsync` | `IIntroSkipperDatabase.GetSegmentsAsync` (compiled query) | `SegmentProvider` injected; `BaseItemAnalyzerTask`/`SegmentEditorController` via bridge |
| `DeleteItemSegmentsAsync` | `IIntroSkipperDatabase.DeleteItemSegmentsAsync` | `SegmentProvider` injected |
| `CleanTimestampsAsync` | `IIntroSkipperDatabase.CleanTimestampsAsync` | `CleanCacheTask` injected |
| `SetAnalyzerActionAsync` | `IIntroSkipperDatabase.SetAnalyzerActionAsync` | `VisualizationController` injected |
| `SetEpisodeIdsAsync` | `IIntroSkipperDatabase.SetEpisodeIdsAsync` | bridge (`BaseItemAnalyzerTask`) |
| `RemoveEpisodeIdAsync` | `IIntroSkipperDatabase.RemoveEpisodeIdAsync` | bridge (`SegmentEditorController`) |
| `CleanStaleAutomaticSegmentsAsync` | `IIntroSkipperDatabase.CleanStaleAutomaticSegmentsAsync` | bridge (`BaseItemAnalyzerTask`) |
| `GetEpisodeIdsAsync` | `IIntroSkipperDatabase.GetEpisodeIdsAsync` | bridge (currently unused externally) |
| `GetSettleReanalysisStatesAsync` | `IIntroSkipperDatabase.GetSettleReanalysisStatesAsync` | bridge (`BaseItemAnalyzerTask`) |
| `RecordSettleReanalysisAsync` | `IIntroSkipperDatabase.RecordSettleReanalysisAsync` | bridge (`BaseItemAnalyzerTask`) |
| `ResetSeasonForReanalysisAsync` | `IIntroSkipperDatabase.ResetSeasonForReanalysisAsync` | bridge (`BaseItemAnalyzerTask`) |
| `GetSeasonQueueSnapshotAsync` | `IntroSkipperDatabase.GetSeasonQueueSnapshotAsync` (internal method — `SeasonQueueSnapshot` is an internal type; promote both when `QueueManager` is injected) | bridge (`QueueManager`) |
| `GetAllAnalyzerActionsAsync` | `IIntroSkipperDatabase.GetAllAnalyzerActionsAsync` | `VisualizationController` injected |
| `GetAnalyzerActionAsync` | `IIntroSkipperDatabase.GetAnalyzerActionAsync` | bridge (`BaseItemAnalyzerTask`) |
| `CleanSeasonStateAsync` | `IIntroSkipperDatabase.CleanSeasonStateAsync` | `CleanCacheTask` injected |
| `DeleteTimestampAsync` | `IIntroSkipperDatabase.DeleteTimestampAsync` | bridge (`SegmentEditorController`) |
| `ShouldSettleReanalyze`, `MapSegmentTypeToMode` | stay on `Plugin` — pure functions, no DB access | n/a |

### 2b. Direct `CreateDbContext()` / `CreateCacheDbContext()` call sites

| Call site | Old code | New home |
|---|---|---|
| `SkipIntroController.ResetIntroTimestamps` | inline `ExecuteDeleteAsync` by mode | `IIntroSkipperDatabase.DeleteSegmentsByModeAsync` *(new name for a previously inline op)* |
| `SkipIntroController.RebuildDatabase` | `db.RebuildDatabaseAsync(Plugin.CreateDbContext)` | `IIntroSkipperDatabase.RebuildDatabaseAsync` (facade supplies its own factory) |
| `VisualizationController.EraseSeasonAsync` | inline segment delete + season-state clear | `DeleteSegmentsForItemsAsync` + `ClearSeasonEpisodeIdsAsync` (cache deletion stays interleaved at the call site, same order as before) |
| `VisualizationController.ClearExcludedTimestampsAsync` (segments + season state) | inline delete + episode-list pruning | `DeleteSegmentsForItemsAsync` + `RemoveEpisodeIdsFromSeasonsAsync` |
| `VisualizationController.ClearExcludedTimestampsAsync` (cache) | inline `ExecuteDeleteAsync` | `IDetectionCacheDatabase.DeleteForItemsAsync` |
| `CleanCacheTask` (stale-ID scan) | inline `Select/Distinct/Where` | `IDetectionCacheDatabase.GetStaleItemIdsAsync` |
| `CleanCacheTask` (batch delete) | inline `ExecuteDeleteAsync` | `IDetectionCacheDatabase.DeleteForItemsAsync` |
| `DetectionCacheService.TryRead` | inline `FirstOrDefault` | `IDetectionCacheDatabase.FindEntry` |
| `DetectionCacheService.Write`/`UpsertEntry` | inline upsert + `SaveChanges` | `IDetectionCacheDatabase.Upsert` |
| `DetectionCacheService.DeleteForItem` | inline `ExecuteDelete` | `IDetectionCacheDatabase.DeleteForItem` |
| `DetectionCacheService.DeleteByMode` | inline `ExecuteDelete` | `IDetectionCacheDatabase.DeleteByMode` |
| `DetectionCacheService.HasCachedFingerprint` | inline `Any` | `IDetectionCacheDatabase.HasEntry` |
| `Plugin` ctor (both DB inits) | `EnsureLegacySchemaCompatibility`+`ApplyMigrations` / `EnsureSchema` | `IIntroSkipperDatabase.InitializeAsync` / `IDetectionCacheDatabase.Initialize` (prototype keeps the ctor bootstrap as belt-and-braces; end state deletes it) |

`DetectionCacheService` is **fully migrated** in the prototype: zero
`Plugin.CreateCacheDbContext()` references remain in it. Serialization/compression and
config-hash policy stay in the service; the facade is purely the DB boundary.

### 2c. End state (zero DB code in `Plugin`)

Delete from `Plugin`: all wrappers in §2a, `SegmentDatabase`/`CacheDatabase` bridge
properties, `CreateDbContext`/`CreateCacheDbContext`, the ctor DB bootstrap, and the
`_dbPath`/`_cacheDbPath` fields (path logic already lives in
`IntroSkipperDatabasePaths`). Constructor threading for the manually-`new`-ed chain:

- `BaseItemAnalyzerTask(…, IIntroSkipperDatabase db)` — its three creators
  (`DetectSegmentsTask`, `Entrypoint`, `VisualizationController.ScanSeason`) are all
  DI-resolved and just forward the facade.
- `QueueManager(…, IIntroSkipperDatabase db)` — created by `BaseItemAnalyzerTask`,
  `CleanCacheTask`, `VisualizationController`; all have the facade by then.
- Analyzers (`ChromaprintAnalyzer`, `ChapterAnalyzer`, `BlackFrameAnalyzer`,
  `CreditsBlackFrameAnalyzer`) — created by `BaseItemAnalyzerTask`; add the facade
  parameter alongside the existing `IFFmpegService`/`IDetectionCacheService` params
  (`RecapDetectionHelper` receives it as a method argument).
- `SegmentEditorController` — DI-resolved; inject directly.
- Promote `SeasonQueueSnapshot` to `public` (or keep consumers on the concrete class)
  and lift `GetSeasonQueueSnapshotAsync` onto the interface.

This is ~8 constructor-signature diffs with no logic changes — deliberately left out of
the spike to keep the reviewed surface small, and proven feasible by the five consumers
that were fully migrated.

---

## 3. DI registration (as implemented)

```csharp
// PluginServiceRegistrator.RegisterServices
serviceCollection.AddDbContextFactory<IntroSkipperDbContext>((serviceProvider, options) =>
    options.UseSqlite($"Data Source={IntroSkipperDatabasePaths.GetSegmentDatabasePath(
            serviceProvider.GetRequiredService<IApplicationPaths>())}")
        .AddInterceptors(_pragmaInterceptor));

serviceCollection.AddDbContextFactory<DetectionCacheDbContext>((serviceProvider, options) =>
    options.UseSqlite($"Data Source={IntroSkipperDatabasePaths.GetDetectionCacheDatabasePath(
            serviceProvider.GetRequiredService<IApplicationPaths>())}")
        .AddInterceptors(_pragmaInterceptor));

serviceCollection.AddSingleton<IIntroSkipperDatabase>(sp => new IntroSkipperDatabase(
    sp.GetRequiredService<IDbContextFactory<IntroSkipperDbContext>>(),
    sp.GetRequiredService<ILogger<IntroSkipperDatabase>>()));

serviceCollection.AddSingleton<IDetectionCacheDatabase>(sp => new DetectionCacheDatabase(
    sp.GetRequiredService<IDbContextFactory<DetectionCacheDbContext>>(),
    sp.GetRequiredService<ILogger<DetectionCacheDatabase>>()));

serviceCollection.AddHostedService<IntroSkipperDatabaseInitializer>(); // eager warm-up
serviceCollection.AddHostedService<Entrypoint>();
```

Notes:

- The options callback runs lazily (options are built on first factory use), so
  `IApplicationPaths` is resolvable and `Plugin.Instance` is **not** needed —
  registration works even though the plugin instance is created after the container.
- `IntroSkipperDatabasePaths` is the single source of truth for
  `<DataPath>/introskipper/introskipper.db` / `introskipper-cache.db`; the `Plugin`
  ctor uses the same helper, so DI and plugin can never point at different files.
- Both factories share one stateless `SqlitePragmaInterceptor`
  (`PRAGMA busy_timeout=5000` on every connection open), preserving today's behavior
  for factory-created contexts. Path-ctor contexts keep their own interceptor via
  `OnConfiguring` (unchanged).
- The generic EF service types (`DbContextOptions<TContext>`,
  `IDbContextFactory<TContext>`) are keyed by our context types, so registering them in
  Jellyfin's host container cannot collide with Jellyfin's own EF registrations.

Transitional bridge: `Plugin.SegmentDatabase` / `Plugin.CacheDatabase` are lazily
created facade instances over `IntroSkipperDbContextPathFactory(() => DbPath)`. The
facades are stateless, so the DI instance and the bridge instance coexist safely (same
file, same pragmas, same short-lived contexts as today). The bridge disappears in the
end state.

---

## 4. Initialization / lifecycle design and ordering proof

**Chosen option: lazy `Task`-gate inside the facade + eager `IHostedService` warm-up.**
(Rejected: "keep bootstrap only in `Plugin` ctor" — leaves ordering dependent on plugin
activation time, which is exactly the implicit coupling we're removing; "hosted-service
init only" — Jellyfin can hit `SegmentProvider`/controllers before hosted services
start, so a hosted service alone cannot guarantee ordering.)

Mechanism (`IntroSkipperDatabase`):

```csharp
_initialization = new Lazy<Task>(InitializeCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
public Task InitializeAsync() => _initialization.Value;
private Task EnsureInitializedAsync() => _initialization.Value;   // awaited first in EVERY operation
// InitializeCoreAsync: EnsureLegacySchemaCompatibility() then Database.MigrateAsync(), errors logged
```

**Ordering proof.** Every public data operation begins with
`await EnsureInitializedAsync()`. `LazyThreadSafetyMode.ExecutionAndPublication`
guarantees `InitializeCoreAsync` is invoked exactly once and all racers receive the
*same* `Task` instance; a `Task` cannot complete before its body finishes; therefore no
query in any facade operation can be issued before legacy repair + `MigrateAsync` have
returned — for every possible interleaving of first callers (early playback through
`SegmentProvider`, an early controller hit, the hosted warm-up, or a scheduled task).
The hosted initializer merely moves the one-time cost off the first request. The same
argument applies to the cache facade with a synchronous `Lazy<bool>` gate around
`EnsureSchema()` (sync because all cache consumers are sync today).

**Failure semantics** are preserved from the current `Plugin` ctor: initialization
exceptions are logged (`Error initializing database`) and swallowed; subsequent
operations run against whatever schema exists and surface their own errors. The gate
does not retry — identical to today, where the ctor attempt is also one-shot. This is a
deliberate parity decision, recorded as risk R4.

**Belt-and-braces in the prototype:** the `Plugin` ctor bootstrap is retained (it runs
`Database.Migrate()` which is idempotent, and `EnsureLegacySchemaCompatibility` is
written to be re-runnable). Tests prove the gate alone is sufficient
(`InitializationGate_CreatesSchemaBeforeFirstQuery` runs the facade against a virgin
file with no ctor bootstrap at all). The end state deletes the ctor bootstrap.

**Rebuild and legacy repair** keep working unchanged: `RebuildDatabaseAsync(bool, ct)`
wraps the existing context-level salvage flow, passing `_contextFactory.CreateDbContext`
as the sibling-context factory; `EnsureLegacySchemaCompatibility` is called by the gate
exactly as the ctor called it. The EF migration history behavior is untouched — the
gate calls the same `ApplyMigrationsAsync` (`Database.MigrateAsync`) against the same
migration set, and the design-time factory `IntroSkipperDbContextFactory` is unmodified,
so `dotnet ef migrations` still works.

---

## 5. Transactions & SQLite concurrency strategy

Unchanged by design — the facade preserves the current model verbatim:

- **Short-lived contexts, no shared state.** Singleton facades hold no context; each
  operation opens/closes its own. Safe under `Parallel.ForEachAsync` in analysis
  (multiple concurrent operations = multiple connections, exactly as today).
- **Explicit transactions only where invariants need atomicity:** the non-commercial
  `UpdateTimestampAsync` path (read-check-replace) and `ResetSeasonForReanalysisAsync`
  (segment delete + episode-list clear must commit together). Everything else relies on
  per-`SaveChanges`/per-`ExecuteDelete` implicit transactions, as before.
- **Writer contention:** WAL journal mode + the shared `busy_timeout=5000` pragma
  interceptor on every connection open. Verified: EF's SQLite provider sets
  `journal_mode=wal` when it *creates* a database (via `Migrate`/`EnsureCreated`), and
  WAL is a persistent database property — both plugin databases are always created by
  EF, so no per-connection enforcement is needed. Single-statement `EF.Parameter`
  deletes keep individual write transactions short, bounding writer-lock hold times.
- **Cross-database consistency** stays best-effort and call-site-ordered (segments
  first, cache second, with `CancellationToken.None` on the cache leg) — the facade
  deliberately does not pretend to offer cross-DB transactions.

---

## 6. EF Core 10 feature verdicts

| Feature | Verdict | Why |
|---|---|---|
| `AddPooledDbContextFactory` | **Don't use** | Pooling requires a single public options-ctor and skips `OnConfiguring`, which would force removing the string-path ctor that the design-time factory, `RebuildDatabaseAsync`'s file-delete fallback, the transitional bridge, and ~30 existing tests construct directly. The payoff is µs-scale context reuse on a plugin whose query rate is a handful per playback/scan — unmeasurable here. Interceptor note: our interceptor is stateless so pooling *would* be compatible, but that only removes one of the blockers. |
| `AddDbContextFactory` (non-pooled) | **Use** (implemented) | Gives the facades a DI-native, test-replaceable context source; options built once; keeps both ctors legal. |
| EF10 parameterized-collection translation (with padding) vs manual `Chunk(500)` | **Superseded in round 2: use `EF.Parameter` (json_each), chunking removed** | Round-1 measurement of the *default* translation stands (EF 10.0.7 + Microsoft.Data.Sqlite + e_sqlite3 3.50.3: discrete padded parameters, 501 elements → 550 params, hard failure above `SQLITE_MAX_VARIABLE_NUMBER` = 32,766), but it missed `EF.Parameter(collection)`, which forces the single-JSON-parameter `json_each` translation on SQLite. Re-measured on the same stack (40k-row table): `EF.Parameter` Contains at 33k IDs = one bound parameter, 5 runs in 245 ms vs 5,184 ms for Chunk(500) (~21x); 33k NOT-IN `ExecuteDelete` = 90 ms, correct row counts. All large-set ops now use `EF.Parameter` and the chunk helper is deleted (details in Round 2 revisions). `json_each` availability is guaranteed because the plugin bundles `SQLitePCLRaw.lib.e_sqlite3` — the system SQLite is never used. Verified by 33,000-ID pin tests on every converted operation. |
| `ExecuteUpdate`/`ExecuteDelete` | **Keep/expand `ExecuteDelete`; no `ExecuteUpdate` yet** | All bulk deletes already use `ExecuteDeleteAsync` and moved verbatim. The natural `ExecuteUpdate` candidates (`ClearSeasonEpisodeIdsAsync`, `RemoveEpisodeIdsFromSeasonsAsync`) write the value-converted `EpisodeIds` column; setting converted JSON columns via `ExecuteUpdate` works for constants but the `RemoveEpisodeIds` case needs per-row list surgery, which SQL can't express without JSON functions. Kept load-modify-save for parity; noted as a follow-up micro-optimization for the clear-only path. The EF10 non-expression setter overload would only help if we built setters conditionally — we don't. |
| Compiled queries (`EF.CompileAsyncQuery`) | **Use for the playback hot path only** (implemented) | `GetSegmentsAsync` runs on every `SegmentProvider.GetMediaSegments` call (every playback) and `GetTimestampsAsync` reuses it. One static compiled delegate eliminates repeated LINQ translation. Not applied elsewhere: scan-time queries run a few times per season; the readability tax isn't paid back. |
| Named query filters | **Don't use** | No global filters exist; the only recurring predicate (`IsUserProvided`) is a *write-path rule*, not a read filter — hiding it in a query filter would obscure the invariant and require `IgnoreQueryFilters` on most reads. |
| `LeftJoin` operator | **Don't use** | The schema has two unrelated aggregates; no query joins them. The one multi-source read (`GetSeasonQueueSnapshotAsync`) intentionally issues separate queries per table. |
| Compiled models | **Don't use** | Per the shared guidance: 3 entities; model building is a one-time ~ms cost at first context creation. No measurable benefit to argue. |

---

## 7. Testing strategy

- **Facades are constructible without `Plugin.Instance` or DI:** any
  `IDbContextFactory<T>` works. Tests use `IntroSkipperDbContextPathFactory` over a
  temp file (`DatabaseTestHelpers.CreateSegmentDatabase(path)`), matching the repo's
  existing temp-file SQLite pattern.
- **New tests (`TestDatabaseFacades`, 15 tests):**
  - `InitializationGate_CreatesSchemaBeforeFirstQuery` — ordering proof in executable
    form: virgin file, no explicit init, first query succeeds and migrations are applied.
  - Domain invariant 1: analysis result does not overwrite a user-provided segment
    (+ inverse: user write replaces analysis result).
  - Domain invariant 2: credits/intro overlap guard (3-case theory, mirrors the
    existing `Plugin`-level theory).
  - Chunk-free large-set pins at 33,000 IDs (> 32,766) for `CleanTimestampsAsync`,
    `DeleteSegmentsForItemsAsync`, `CleanSeasonStateAsync`, `CleanStaleAutomaticSegmentsAsync`,
    `ResetSeasonForReanalysisAsync`, `GetSeasonQueueSnapshotAsync`, and the cache
    facade's stale-scan + batch delete.
  - Concurrent-initialization pin: two facade instances over one legacy-shaped file
    initialize concurrently without errors or data loss.
  - Commercial multi-segment insert + dedup (filtered-unique-index invariant at the
    facade level).
- **Existing tests kept green (377/378)** — including every legacy-schema,
  rebuild, cache-operation, and controller test. Controller tests now construct the
  controllers with plugin-bound facades whose path resolver follows
  `Plugin.Instance` lazily, preserving the test suite's instance-swapping pattern.
  The `Plugin`-level theory `UpdateTimestampAsync_CreditsOverlapGuard` still passes
  *through the delegating bridge*, proving behavioral parity of the moved code.
- **Sync-completion property preserved:** `TestSkipIntroController` asserts the
  refresher is reached synchronously before the action task completes; it passes, which
  demonstrates the init gate (SQLite async ops complete synchronously) does not change
  the observable async shape of the request path.

---

## 8. Risk register

| # | Risk | Mitigation |
|---|---|---|
| R1 | **Double initialization** during transition (Plugin ctor + facade gate; DI facade + bridge facade are distinct instances with distinct gates) | `Database.Migrate` is idempotent; `EnsureLegacySchemaCompatibility` is re-runnable by construction (existence checks before every step); `EnsureSchema` is `EnsureCreated`-based. Verified: full suite passes with both paths active. End state removes ctor bootstrap and bridge. |
| R2 | **Interface bloat / god-class drift** (23 methods and could grow) | Conventions in §1 (aggregate-partials, naming, no-pass-through rule, pre-agreed split seam at >15 methods per file). |
| R3 | **Bridge lifetime**: `Plugin.SegmentDatabase` caches the facade; a test swapping `_dbPath` after first use would silently target the old… no — path is resolved per `CreateDbContext()` call via `IntroSkipperDbContextPathFactory(() => DbPath)` | Lazy path resolution makes the bridge follow field swaps; covered implicitly by every existing controller/plugin test. |
| R4 | **Init gate masks a broken DB forever** (one-shot, failures swallowed) | Parity with today's ctor behavior (see §4). Documented alternative for post-spike discussion: allow `RebuildDatabaseAsync` to reset the gate. |
| R5 | **Early caller pays migration latency** on first playback if it beats the hosted warm-up | Warm-up hosted service is registered first; worst case equals today's plugin-ctor cost, just relocated; measured migration no-op check is ~ms. |
| R6 | **Two hosted services ordering** (initializer vs `Entrypoint`) | `Entrypoint` performs no DB work in `StartAsync`; even if started concurrently, any DB call it triggers goes through the gate. |
| R7 | **`AddDbContextFactory` in Jellyfin's shared container** could surprise (e.g., another plugin registering the same context type — impossible; or scoped-lifetime interactions) | Factory + options registered singleton against plugin-owned context types; no scoped `DbContext` registration is added, so nothing changes for Jellyfin's own EF usage. |
| R8 | **Behavioral deltas introduced knowingly**: large-set ops now translate through `EF.Parameter`/`json_each` (single statement) instead of the original loops; `VisualizationController`'s season-state clear commits separately from the segment delete (it already did — two implicit transactions — the facade preserves that) | Row sets affected are provably identical (same predicates); crash behavior only improves (three latent >32k-parameter sites fixed). Pinned by 33,000-ID tests on every converted op. |
| R9 | **`SeasonQueueSnapshot` internal type** blocks interface completeness | Documented promotion path (§2c); concrete-class internal method keeps compile-time safety meanwhile. |

---

## 9. Honest cons of this architecture

Argued for comparison against the rival spikes:

1. **The interface is wide and will get wider.** 23 methods today, and every new
   query shape (e.g. a future "get segments for many items") lands on the same
   interface. Layered stores would give each concern a narrower contract; a
   no-repository design would have zero contract. The partial-file conventions manage
   this but do not eliminate it — this is the structural weakness of Theory B.
2. **One mock to rule them all.** Consumers that want unit tests must fake a large
   interface (or use the real facade over temp SQLite, which is what this repo does —
   mitigating but not eliminating the concern).
3. **Method-name proliferation for call-site one-liners.** `DeleteSegmentsByModeAsync`,
   `ClearSeasonEpisodeIdsAsync`, `RemoveEpisodeIdsFromSeasonsAsync` exist because raw
   context access at three call sites had to go *somewhere*. A no-repository design
   (inject `IDbContextFactory` everywhere) would not need these names — at the price of
   re-scattering the invariants.
4. **The facade hides query cost.** A caller can't see that one method is a
   read-modify-write loop while another is a single `ExecuteDelete`; with raw context
   access the cost is visible at the call site.
5. **Cross-DB orchestration still lives in callers.** The facade-per-database boundary
   means "erase season + cache" ordering rules remain duplicated at call sites
   (`SkipIntroController`, `VisualizationController`). A single higher-level service
   could own that — but would be a third layer.
6. **Transitional period has two facade instances** (DI + Plugin bridge). Harmless
   (stateless over the same file) but conceptually ugly until the constructor threading
   in §2c lands.

---

## 10. Prototype file manifest

**New:** `Db/IIntroSkipperDatabase.cs`, `Db/IntroSkipperDatabase.cs`,
`Db/IntroSkipperDatabase.Segments.cs`, `Db/IntroSkipperDatabase.SeasonStates.cs`,
`Db/IntroSkipperDatabase.Maintenance.cs`, `Db/IDetectionCacheDatabase.cs`,
`Db/DetectionCacheDatabase.cs`, `Db/IntroSkipperDatabasePaths.cs`,
`Db/IntroSkipperDbContextPathFactory.cs`, `Db/DetectionCacheDbContextPathFactory.cs`,
`Db/DatabaseInitializationLocks.cs`, `Services/IntroSkipperDatabaseInitializer.cs`,
tests `TestDatabaseFacades.cs`, `DatabaseTestHelpers.cs`.

**Modified:** `Plugin.cs` (bodies → delegating wrappers + bridge),
`PluginServiceRegistrator.cs`, `Providers/SegmentProvider.cs`,
`Controllers/SkipIntroController.cs`, `Controllers/VisualizationController.cs`,
`ScheduledTasks/CleanCacheTask.cs`, `FFmpeg/DetectionCacheService.cs`,
test constructors in `EntrypointTestHelpers.cs`, `TestSkipIntroController.cs`,
`TestVisualizationController.cs`, `TestCacheOperations.cs`, `TestAudioFingerprinting.cs`,
`TestBlackFrames.cs`, `TestFFmpegService.cs`.

**Untouched (by design):** both `DbContext`s, entities, migrations,
`IntroSkipperDbContextFactory` (design-time), `SqlitePragmaInterceptor`/`SqlitePragmas`.

---

## 11. Round 2 revisions (coordinator feedback)

### 11.1 Hosted warm-up can no longer abort Jellyfin host startup (item 1, fixed)

Confirmed finding. Two-layer fix:

1. **`IntroSkipperDatabaseInitializer.StartAsync` is independently exception-proof:**
   each facade warm-up call is wrapped in its own catch-all with a `LoggerMessage`
   warning. Blast-radius reasoning (also in the code): this method runs inside
   Jellyfin's host startup, so an unhandled exception aborts the *entire server* —
   every plugin plus Jellyfin itself — over what is, at worst, a plugin cache file.
   The facades log-and-swallow their own init failures, but that is their contract,
   not a guarantee the warm-up may rely on; the warm-up must be safe against facade
   bugs and exotic escapes on its own.
2. **`DetectionCacheDatabase.InitializeCore` catch filter broadened** from
   `IOException or SqliteException` to catch-all log-and-continue. The old filter let
   `UnauthorizedAccessException` (from `EnsureDeleted`'s file delete during corruption
   recovery), `InvalidOperationException`, etc. escape — and because the gate is
   `Lazy<bool>` with `ExecutionAndPublication`, an escaping exception would be
   **cached and rethrown on every subsequent cache operation**, permanently poisoning
   the cache for the process lifetime. With catch-all, neither gate
   (`Lazy<Task>` for segments already had catch-all; `Lazy<bool>` for cache now too)
   can cache a fault. Degradation path on failure: identical to the pre-refactor
   plugin-constructor behavior — operations run against whatever schema exists and
   surface their own errors.

### 11.2 Chunking verdict superseded: `EF.Parameter` adopted (item 2)

The round-1 verdict measured the default translation and `EF.Constant` but not
`EF.Parameter(collection)`, which forces the single-JSON-parameter `json_each`
translation on SQLite. Re-measured on the exact stack (EF 10.0.7,
Microsoft.Data.Sqlite, bundled e_sqlite3 3.50.3; 40,000-row table):

| Operation (33k-ID set) | Result |
|---|---|
| `EF.Parameter` `Contains` (SELECT) | one bound parameter, translation contains `json_each`, correct count, 5 runs = **245 ms** |
| Chunk(500) `Contains` (SELECT), 5 runs | **5,184 ms** (~21x slower) |
| `ExecuteDelete` with `!EF.Parameter(ids).Contains(...)` | **90 ms**, correct rows deleted |
| Default translation at 33k | `SQLite Error 1: 'too many SQL variables'` (unchanged from round 1) |

The round-1 statement-cache argument applied to padding of the *default* discrete-
parameter translation and is moot under `json_each` (one statement shape, one
parameter). Conceded and adopted — the numbers match the coordinator's benchmark
within noise. Converted operations (all single-statement now, chunk helper deleted):

- `IntroSkipperDatabase`: `CleanTimestampsAsync` (restored to a single NOT-IN delete —
  simpler than the original read-filter-delete loop), `DeleteSegmentsForItemsAsync`,
  `CleanStaleAutomaticSegmentsAsync`, `ResetSeasonForReanalysisAsync`,
  `GetSeasonQueueSnapshotAsync` (single read), `RemoveEpisodeIdsFromSeasonsAsync`
  (single read), `CleanSeasonStateAsync` (restored to the original single NOT-IN shape,
  now safe at any size).
- `DetectionCacheDatabase`: `DeleteForItemsAsync`, `GetStaleItemIdsAsync` (restored to
  the original server-side NOT-IN shape).

Small captured collections (the ≤5-element `modeArray.Contains`) intentionally keep the
default translation — discrete parameters with padding are ideal at that size.
`json_each` availability is not a portability risk: the plugin bundles
`SQLitePCLRaw.lib.e_sqlite3`, so the system SQLite build is never used.

Pin tests at 33,000 IDs (> 32,766) now cover **every** converted operation:
`CleanTimestampsAsync`, `DeleteSegmentsForItemsAsync`, `CleanSeasonStateAsync`,
`CleanStaleAutomaticSegmentsAsync`, `ResetSeasonForReanalysisAsync`,
`GetSeasonQueueSnapshotAsync`, cache stale-scan + `DeleteForItemsAsync`
(plus the pre-existing `Plugin`-level 33k test, which now exercises the same path
through the bridge).

### 11.3 Concurrent legacy-repair race: empirically characterized, gated anyway (item 3)

Probe: two concurrent connections against one legacy-shaped WAL database, each running
`BEGIN; <column-existence check>; ALTER TABLE ADD COLUMN; COMMIT;` with a barrier
forcing maximal overlap, 10 iterations.

**Result: the hypothesized race does not manifest — 10/10 runs, zero errors, exactly
one column added.** Root cause: the hypothesis assumed *deferred* transactions (both
racers pass the check before either writes). Microsoft.Data.Sqlite's
`BeginTransaction()` issues **`BEGIN IMMEDIATE`** — the write lock is acquired at
`BEGIN`, *before* the existence checks run, so check-then-ALTER is atomic per
transaction and strictly serialized between connections. The instrumented probe shows
the loser's check observing `exists=True` after the winner's commit (Microsoft.Data.Sqlite
additionally retries `SQLITE_BUSY` on `BEGIN` up to `CommandTimeout`, on top of
`busy_timeout`). `EnsureLegacySchemaCompatibility` runs its checks inside exactly such
a transaction, so it inherits this serialization. Concurrent `MigrateAsync` is
separately protected by EF Core 9+'s migration database lock.

**Defense-in-depth added regardless:** `DatabaseInitializationLocks` — a process-wide
`ConcurrentDictionary<string, SemaphoreSlim>` keyed by normalized database file path
(derived from the context's connection string) — serializes the *entire* initialization
sequence (repair + migrate, or cache `EnsureSchema` incl. delete-and-recreate recovery)
across the DI singleton and the transitional `Plugin` bridge. Rationale: the safety
argument becomes local instead of resting on two external behaviors (MDS's
`BEGIN IMMEDIATE` default and EF's migration lock), it prevents interleaved warning
logs from the two initializers, and it protects the cache's non-transactional
delete-and-recreate recovery path, which the `BEGIN IMMEDIATE` argument does not cover.
Cross-*process* races (two Jellyfin instances sharing one DB file) are out of scope —
unchanged from the status quo, and partially covered by the same `BEGIN IMMEDIATE`
serialization. Pinned by `ConcurrentFacadeInstances_InitializeLegacyDatabaseWithoutErrors`
(two facades, one legacy-shaped file, concurrent first operations).

### 11.4 WAL journal mode (item 4)

Doc note added in §5: EF's SQLite provider sets `journal_mode=wal` when it creates a
database (`Migrate`/`EnsureCreated`), and WAL is a persistent database property. Both
plugin databases are always EF-created (including the rebuild and cache-recreate
paths), so no per-connection or init-time enforcement is added.

### 11.5 Delta summary

Code: `Services/IntroSkipperDatabaseInitializer.cs` (exception-proof warm-up + logger),
`Db/DetectionCacheDatabase.cs` (catch-all init, `EF.Parameter`, init lock),
`Db/IntroSkipperDatabase.cs` (init lock, `SqliteParameterBatchSize` removed),
`Db/IntroSkipperDatabase.{Segments,SeasonStates,Maintenance}.cs` (`EF.Parameter`),
`Db/DatabaseInitializationLocks.cs` (new), interface doc updates, 5 new tests
(15 total in `TestDatabaseFacades`). Verification: full suite 382/383 — sole failure
remains the known environmental `TestSilenceDetection`.
