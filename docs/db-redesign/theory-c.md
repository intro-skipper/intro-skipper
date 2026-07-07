# Theory C: No Repository — `DbContext` *is* the Repository

**Status:** exploration spike (branch `capy/theory-c-db-layer`, round 2), competing with
two rival designs (layered stores, facade). Prototype compiles StyleCop-clean and passes
the full test suite (379 passing; only the known environmental
`TestAudioFingerprinting.TestSilenceDetection` failure remains). Round-2 coordinator
feedback is addressed in §12.

---

## 1. Architecture overview

EF Core's `DbContext` already implements the Repository and Unit-of-Work patterns
(`DbSet<T>` = repository, `SaveChanges` = unit of work). The EF team's own guidance is to
not wrap it again. Theory C takes that position literally for this plugin:

1. **Consumers inject `IDbContextFactory<IntroSkipperDbContext>` and/or
   `IDbContextFactory<DetectionCacheDbContext>` directly.** There is no `ISegmentStore`,
   no `IIntroSkipperRepository`, no facade. The factory interface *is* the data-access seam.
2. **Shared multi-step operations become extension methods on the context**, grouped by
   aggregate into static classes in `IntroSkipper.Db`:
   - `SegmentOperations` — everything touching `DbSegment`
   - `SeasonStateOperations` — everything touching `DbSeasonState`
3. **Single-query call sites inline their LINQ** against a factory-created context
   (e.g. `ResetIntroTimestamps`' one-line `ExecuteDelete`).
4. **Lifecycle has one owner**: `DatabaseInitializer` (a plain DI singleton) owns
   migrations, legacy schema repair, cache create-or-recover, and rebuild. Two thin
   `IDbContextFactory<T>` implementations (`GatedIntroSkipperDbContextFactory`,
   `GatedDetectionCacheDbContextFactory`) await the initializer before handing out any
   context — this is the ordering proof (§5).

```
Consumers (controllers, SegmentProvider, tasks, analyzers, DetectionCacheService)
        │  ctor-inject
        ▼
IDbContextFactory<TContext>  ──implemented by──►  Gated*DbContextFactory
        │  CreateDbContext[Async]()                      │ awaits (once)
        ▼                                                ▼
   TContext (DbContext)                          DatabaseInitializer
        │  extension methods                     (legacy repair → Migrate;
        ▼                                         cache EnsureSchema/recover;
SegmentOperations / SeasonStateOperations         RebuildDatabaseAsync)
   (domain invariants live here)
```

### Extension methods vs. static operation classes — chosen convention

Chosen: **extension methods on the context, grouped into per-aggregate static classes**
(`db.UpdateTimestampAsync(...)`, `db.GetSeasonQueueSnapshotAsync(...)`).

Why extensions rather than `SegmentOperations.UpdateTimestampAsync(db, ...)` static calls:

- **Discoverability.** IntelliSense on `db.` surfaces the whole operation vocabulary to
  anyone holding a context. This directly answers the "how do I find operations?"
  objection — the context is the API surface, exactly as EF intends.
- **Composability.** An operation works inside *whatever context the caller already has*,
  so several operations can share one context/transaction. The prototype exploits this:
  `SkipIntroController.UpdateTimestampsAsync` now writes all five segment types through a
  single context instead of the five contexts the old `Plugin` methods opened.
- The grouping into `SegmentOperations`/`SeasonStateOperations` keeps files reviewable and
  gives the invariants a named home to cite in review comments.

Why not extensions on `IDbContextFactory<T>` (one-liner call sites): it hides context
lifetime, forbids sharing a context across operations, and reintroduces a de-facto
repository with a different spelling.

### The inline-vs-shared line

- **Inline is fine:** single-site *reads* and single-site *deletes* expressed as one LINQ
  statement (`ExecuteDelete`, `Any`, `FirstOrDefault`). Example: `ResetIntroTimestamps`.
- **Must be a shared operation:** (a) anything that **inserts or replaces `DbSegment`
  rows** — that is exclusively `SegmentOperations.UpdateTimestampAsync`, the invariant
  home (§6); (b) any multi-statement flow used from ≥ 2 call sites; (c) anything whose ID
  collection can be unbounded (the `EF.Parameter`/`json_each` translation policy must not
  be re-derived at call sites).
- `DetectionCacheService` keeps its inline LINQ: it *is* the single consumer-facing owner
  of the cache table plus its compression/config-hash logic; wrapping it again would be
  the repository anti-pattern this theory rejects.

---

## 2. Complete migration inventory

"Prototype" = migrated in this spike. "Delegating" = the `Plugin` method body is now a
2-line delegation to the new home (zero query logic remains in `Plugin.cs`); the end state
deletes the delegator and injects the factory at the listed consumers.

### The 18 `Plugin` DB methods

| # | Current `Plugin` member | New home | Consumers to rewire in end state | Status |
|---|---|---|---|---|
| 1 | `UpdateTimestampAsync` | `SegmentOperations.UpdateTimestampAsync` (ext) — invariant home | SkipIntroController (**done**), SegmentEditorController, ChapterAnalyzer, ChromaprintAnalyzer, BlackFrameAnalyzer, BaseItemAnalyzerTask | Prototype + delegating |
| 2 | `GetTimestampsAsync` | `SegmentOperations.GetTimestampsAsync` (ext) | SkipIntroController (**done**), RecapDetectionHelper | Prototype + delegating |
| 3 | `GetSegmentsAsync` | `SegmentOperations.GetSegmentsAsync` (ext, compiled query) | SegmentProvider (**done**), SegmentEditorController, BaseItemAnalyzerTask | Prototype + delegating |
| 4 | `DeleteItemSegmentsAsync` | `SegmentOperations.DeleteItemSegmentsAsync` (ext) | SegmentProvider (**done**) | Prototype + delegating |
| 5 | `DeleteTimestampAsync` | `SegmentOperations.DeleteTimestampAsync` (ext) | SegmentEditorController | Delegating |
| 6 | `CleanTimestampsAsync` | `SegmentOperations.CleanTimestampsAsync` (ext, `EF.Parameter` NOT-IN delete) | CleanCacheTask | Prototype (op + tests) + delegating |
| 7 | `CleanStaleAutomaticSegmentsAsync` | `SegmentOperations.CleanStaleAutomaticSegmentsAsync` (ext, `EF.Parameter` IN delete) | BaseItemAnalyzerTask | Prototype (op + tests) + delegating |
| 8 | `SetAnalyzerActionAsync` | `SeasonStateOperations.SetAnalyzerActionAsync` (ext) | VisualizationController | Delegating |
| 9 | `SetEpisodeIdsAsync` | `SeasonStateOperations.SetEpisodeIdsAsync` (ext) | BaseItemAnalyzerTask | Delegating |
| 10 | `RemoveEpisodeIdAsync` | `SeasonStateOperations.RemoveEpisodeIdAsync` (ext) | SegmentEditorController | Delegating |
| 11 | `GetEpisodeIdsAsync` | `SeasonStateOperations.GetEpisodeIdsAsync` (ext) | (currently unused externally) | Delegating |
| 12 | `GetSettleReanalysisStatesAsync` | `SeasonStateOperations.GetSettleReanalysisStatesAsync` (ext) | BaseItemAnalyzerTask | Delegating |
| 13 | `RecordSettleReanalysisAsync` | `SeasonStateOperations.RecordSettleReanalysisAsync` (ext) | BaseItemAnalyzerTask | Delegating |
| 14 | `ResetSeasonForReanalysisAsync` | `SeasonStateOperations.ResetSeasonForReanalysisAsync` (ext, `EF.Parameter` + transaction) | BaseItemAnalyzerTask | Delegating |
| 15 | `GetSeasonQueueSnapshotAsync` | `SeasonStateOperations.GetSeasonQueueSnapshotAsync` (ext, `EF.Parameter`) | QueueManager | Delegating |
| 16 | `GetAllAnalyzerActionsAsync` | `SeasonStateOperations.GetAllAnalyzerActionsAsync` (ext) | VisualizationController | Delegating |
| 17 | `GetAnalyzerActionAsync` | `SeasonStateOperations.GetAnalyzerActionAsync` (ext) | BaseItemAnalyzerTask | Delegating |
| 18 | `CleanSeasonStateAsync` | `SeasonStateOperations.CleanSeasonStateAsync` (ext) | CleanCacheTask | Delegating |

Non-DB members that merely live near them: `ShouldSettleReanalyze` (pure set comparison)
and `MapSegmentTypeToMode` stay put (end state: move to `IntroSkipper.Data` helpers —
they have no business on `Plugin` either, but they are not DB code).

### Direct context call sites

| Call site | Today | End-state home | Status |
|---|---|---|---|
| `SkipIntroController.ResetIntroTimestamps` | `Plugin.CreateDbContext()` + `ExecuteDelete` | **Inline** against injected factory (single-query delete) | **Done** |
| `SkipIntroController.RebuildDatabase` | `db.RebuildDatabaseAsync(Plugin.CreateDbContext)` | `DatabaseInitializer.RebuildDatabaseAsync()` (lifecycle owner) | **Done** |
| `VisualizationController.EraseSeasonAsync` | segment `ExecuteDelete` + season-state clear on one context | Now on injected factory (async creation); extraction into shared op `SeasonStateOperations.EraseSeasonDataAsync(db, seasonId, episodeIds)` remains the end-state target; cache deletion stays in the controller (cross-store orchestration is caller concern) | **Factory-injected (R2)** |
| `VisualizationController.ClearExcludedTimestampsAsync` (segment db) | inline multi-statement | Now on injected factory (async creation); extraction into shared op `SeasonStateOperations.ClearEpisodesAsync(db, idsBySeason)` remains the end-state target | **Factory-injected (R2)** |
| `VisualizationController.ClearExcludedTimestampsAsync` (cache db) | `Plugin.CreateCacheDbContext()` + `ExecuteDelete` | Inline against injected `IDbContextFactory<DetectionCacheDbContext>` | **Done (R2)** |
| `CleanCacheTask` (2 cache sites) | `Plugin.CreateCacheDbContext()` | Inline against injected cache factory (single-query read + `EF.Parameter` delete) | **Done (R2)** |
| `DetectionCacheService` (5 sites) | `Plugin.CreateCacheDbContext()` | Injected `IDbContextFactory<DetectionCacheDbContext>`, LINQ stays inline in the service | **Done (all 5)** |
| `Plugin` ctor DB bootstrap | sync migrate + legacy repair + cache EnsureSchema | `DatabaseInitializer` + hosted kick-off + factory gate | **Done** |

### Threading dependencies through manually-constructed objects (end state)

`BaseItemAnalyzerTask`, `QueueManager`, and the analyzers are `new`-ed, not DI-resolved.
The factory is a **singleton**, so threading it is mechanical — every constructor gains an
`IDbContextFactory<IntroSkipperDbContext>` parameter supplied by the DI-resolved roots:

- `Entrypoint`, `DetectSegmentsTask`, `CleanCacheTask`, `VisualizationController` (all
  DI-resolved) inject the factory and pass it to `new BaseItemAnalyzerTask(..., dbFactory)`
  and `new QueueManager(..., dbFactory)`.
- `BaseItemAnalyzerTask` passes it to `new ChapterAnalyzer(..., dbFactory)`,
  `ChromaprintAnalyzer`, `BlackFrameAnalyzer`, `CreditsBlackFrameAnalyzer`; a
  `dbFactory` parameter is added to `RecapDetectionHelper.GetMaximumBoundaryAsync`.
- Loggers already travel exactly this way in the current code (`_loggerFactory.CreateLogger<…>()`),
  so this adds one parameter to constructors that already take six to eight.

No `Plugin.Instance` DB access remains in the end state; `Plugin.CreateDbContext()` /
`CreateCacheDbContext()` and all 18 delegators are deleted.

---

## 3. DI registration (as implemented)

```csharp
// PluginServiceRegistrator.RegisterServices
serviceCollection.AddSingleton<DatabaseInitializer>();
serviceCollection.AddHostedService<DatabaseInitializationService>();
serviceCollection.AddDbContextFactory<IntroSkipperDbContext, GatedIntroSkipperDbContextFactory>(
    (serviceProvider, options) => IntroSkipperDatabase.ConfigureSqlite(
        options,
        IntroSkipperDatabase.GetSegmentDatabasePath(serviceProvider.GetRequiredService<IApplicationPaths>())));
serviceCollection.AddDbContextFactory<DetectionCacheDbContext, GatedDetectionCacheDbContextFactory>(
    (serviceProvider, options) => IntroSkipperDatabase.ConfigureSqlite(
        options,
        IntroSkipperDatabase.GetCacheDatabasePath(serviceProvider.GetRequiredService<IApplicationPaths>())));
```

Notes:

- `AddDbContextFactory<TContext, TFactory>` is the EF-sanctioned hook for a custom factory:
  it registers `DbContextOptions<TContext>` (singleton) and our gated factory as
  `IDbContextFactory<TContext>` in one idiomatic call. The gated factory constructs
  contexts through the options-based constructor — no EF-internal types involved.
- DB paths are computed lazily from `IApplicationPaths` (resolvable in Jellyfin's
  container) the first time options are built; `IntroSkipperDatabase` is the single source
  of truth for paths + connection config, shared with `Plugin` (which still exposes
  `DbPath`/`CacheDbPath` for diagnostics and tests).
- The shared `SqlitePragmaInterceptor` (busy_timeout=5000) is attached to the singleton
  options in `ConfigureSqlite`; the string-path constructors' `OnConfiguring` keeps
  attaching it for the design-time/test path, so both construction styles produce
  identically-configured connections.
- `DatabaseInitializer` takes the two `DbContextOptions<T>` singletons directly and news
  contexts internally — it is *inside* the data layer and deliberately bypasses the gate
  (it *is* the gate).

---

## 4. Initialization & lifecycle design

**Owner:** `DatabaseInitializer` (DI singleton).

- **Two independent one-time gates** (round-2 revision), one per database, each guarded by
  `lock + Task?` (first caller starts `Task.Run(...)`, everyone else awaits the same task;
  a cancelled waiter abandons the *wait*, never the initialization):
  `EnsureSegmentDatabaseInitialized[Async]` and `EnsureCacheDatabaseInitialized[Async]`,
  plus `EnsureInitializedAsync` (both, used by the hosted kick-off and rebuild).
- `InitializeSegmentDatabaseCore()`: ensure directory, `EnsureLegacySchemaCompatibility()`,
  `Database.Migrate()`. `InitializeCacheDatabaseCore()`: `EnsureSchema()` (EnsureCreated +
  probe + delete-and-recreate on corruption). **Both cores are catch-all log-and-continue**,
  so an init task can never fault: a faulted shared task would otherwise poison every later
  `CreateDbContext` with the cached exception and abort host startup via the hosted service.
  Per-query errors after a failed init surface exactly as they did pre-refactor.
- `RebuildDatabaseAsync(force, ct)` — awaits the gate first (a rebuild must never race
  first-time init), then runs the existing salvage/rebuild flow on `IntroSkipperDbContext`
  unchanged. `SkipIntroController.RebuildDatabase` calls this.
- `DatabaseInitializationService` (`IHostedService`) kicks the gate eagerly at server
  start so the first playback query doesn't pay the migration cost. It is an optimization
  only — correctness never depends on it.

### Ordering proof

Claim: **no query executes before migrations/legacy repair complete.**

1. Every runtime code path obtains a context from `IDbContextFactory<T>` — either by
   direct injection (migrated consumers) or via `Plugin.CreateDbContext()`, which now
   delegates to the injected factory. Verified by grep: no production code constructs
   `IntroSkipperDbContext`/`DetectionCacheDbContext` outside `IntroSkipper.Db` (the
   initializer and the rebuild callback, which run under the gate by construction).
2. Each factory implementation awaits/blocks on **its own database's gate** before
   constructing the context (`GatedIntroSkipperDbContextFactory` →
   `EnsureSegmentDatabaseInitialized[Async]`, `GatedDetectionCacheDbContextFactory` →
   `EnsureCacheDatabaseInitialized[Async]`). Failure domains stay isolated: the two
   databases live in separate files precisely for corruption isolation, and the gates
   match — a cache-init failure cannot block segment access (pinned by test
   `CacheInitializationFailure_DoesNotBlockSegmentDatabaseAccess`).
3. Init is idempotent and single-flight, so concurrent early callers (Jellyfin hitting
   `SegmentProvider` or a controller during startup) all observe the same completed task.
4. Covered by test `GatedFactory_AppliesMigrationsBeforeHandingOutContexts`: on a fresh
   file, the *first* context handed out already reports zero pending migrations.
5. Init never throws (catch-all cores), so `DatabaseInitializationService.StartAsync`
   cannot fault Jellyfin host startup and the gates cannot cache an exception.

Trade-offs acknowledged: the synchronous `CreateDbContext()` path blocks
(`GetAwaiter().GetResult()`) on first use; N concurrent sync callers during a long first
migration would pin N thread-pool threads. Mitigation (round 2): the prototype was audited
so **every async call site creates contexts via `CreateDbContextAsync`** — the 18 `Plugin`
delegators route through a private async creation helper, and `SegmentProvider`,
`SkipIntroController`, `VisualizationController`, and `CleanCacheTask` all await the async
factory path. The sync gate remains only for genuinely synchronous consumers (the
`IDetectionCacheService` surface, whose interface is sync). Init work is synchronous EF
code running on a thread-pool thread with no synchronization context, so the sync path
cannot deadlock. If init *fails*, the gate still completes (logged warning) and later
queries fail individually — matching today's behavior.

Rejected alternatives: keeping bootstrap in the `Plugin` ctor (leaves lifecycle glued to
a god object, and plugin construction order vs. Kestrel startup is Jellyfin-version
dependent — the gate holds under *any* ordering); a bare hosted-service initializer with
no gate (provably racy: Jellyfin can serve `SegmentProvider` before/parallel to hosted
`StartAsync`).

---

## 5. Transactions & SQLite concurrency

- **busy_timeout=5000** applied per connection-open by the shared interceptor (unchanged).
  This remains the primary defense for the plugin's real concurrency pattern:
  `Parallel.ForEachAsync` season analysis writing while playback reads.
- **WAL**: verified on this stack — EF's SQLite provider sets `journal_mode=wal` when it
  creates the database (via migrations), and the setting persists in the file; a fresh raw
  `SqliteConnection` against a migration-created DB reports `PRAGMA journal_mode` = `wal`.
  No pragma needed in the interceptor.
- **Explicit transactions** stay exactly where multi-statement atomicity is required and
  nowhere else: `UpdateTimestampAsync` (read-check-replace of non-commercial segments) and
  `ResetSeasonForReanalysisAsync` (chunked deletes + episode-list clear must commit
  together). Because operations are extensions on the *caller's* context, a future caller
  can wrap several operations in one `db.Database.BeginTransactionAsync()` without any
  layer changes — something the old one-context-per-Plugin-method shape made impossible.
- Each logical operation still uses a short-lived context (create → operate → dispose);
  connections return to Microsoft.Data.Sqlite's pool, keeping write locks brief.
- Cross-database consistency (segment DB vs. cache DB) remains best-effort orchestration
  at call sites, as today; the cache is reconstructable by design.

---

## 6. Domain invariants: single home + bypass prevention

Both invariants live in exactly one place, `SegmentOperations.UpdateTimestampAsync`:

1. user-provided segments are never overwritten by analysis results;
2. auto-detected credits that overlap the stored intro are rejected (user-provided ones
   are allowed).

Multiplicity rules (commercial multi-row with filtered unique index, non-commercial
unique per `(ItemId, Type)`) are enforced *structurally* by the schema's filtered unique
indexes — they survive any bypass.

How bypass is prevented without a repository interface:

- **Stated convention with a named anchor:** the rule ("reads and deletes may be inline;
  `DbSegment.Add`/`AddRange` only inside `SegmentOperations`") is written on the
  `SegmentOperations` class doc, which is the thing reviewers link to.
- **Greppability:** `DbSegment.Add` outside `Db/` is a one-line CI check away; the repo
  already treats warnings as errors and has `RS0030` (banned APIs) pre-wired in
  `.editorconfig` — the upgrade path is adding `Microsoft.CodeAnalysis.BannedApiAnalyzers`
  with `P:IntroSkipper.Db.IntroSkipperDbContext.DbSegment` banned and a `#pragma`
  allowance inside `SegmentOperations`. Deliberately not done in the spike (it also bans
  legitimate inline reads unless the ban is scoped, which needs team buy-in).
- **Honesty:** this is weaker than a compile-time seam. A layered-store design can make
  the `DbSet` unreachable; Theory C cannot, because exposing the context *is the point*.
  Ranked as the design's top risk (§9). Mitigation that keeps the theory intact: tests
  pin both invariants (`TestDbOperations`), so a bypassing call site that corrupts
  precedence semantics gets caught the moment anyone reproduces the flow in a test.

---

## 7. EF Core 10 feature verdicts

| Feature | Verdict | Why |
|---|---|---|
| `AddPooledDbContextFactory` | **Don't use** | Pooling requires a single public options-ctor; our contexts must keep the string-path ctor for the design-time factory-free test convention, `EnsureLegacySchemaCompatibility` tooling, and ~30 existing test files. Pooling also skips per-instance `OnConfiguring` (the string-path config path would silently die) and retains context state across leases. The measured win is ~µs per lease; every operation here does real SQLite I/O (≥100 µs). Interceptors themselves are pooling-compatible (they live on the singleton options), so this is revisitable if the string ctor is ever deleted — but it isn't worth breaking the ctor contract today. |
| EF10 parameterized-collection translation (padding) vs manual chunking | **Use `EF.Parameter` (JSON/`json_each`); chunking removed** (revised in round 2) | Measured on this repo (EF 10.0.7, SQLite, `ToQueryString`): the default mode emits one scalar parameter per element (`WHERE ItemId IN (@ids1…@idsN)`, padded buckets), so the SQLite host-parameter limit (`SQLITE_MAX_VARIABLE_NUMBER` = 32766 since SQLite 3.32; e_sqlite3 3.50.x ships the default) applies and 33k-ID sets throw 'too many SQL variables'. `EF.Parameter(ids)` forces the EF8/9-style translation: a single JSON parameter consumed via `json_each(@ids)` — no parameter limit at any collection size. Round-1 kept `Chunk(500)` as the provider-agnostic option; the round-2 coordinator benchmark on this exact stack flipped the call: 33k-ID `Contains` ×5 = 235 ms via `EF.Parameter` vs 1552 ms chunked (~6.6×), `ExecuteDelete` NOT-IN at 33k = 218 ms. All five unbounded-collection sites (`CleanTimestampsAsync` — now a single NOT-IN `ExecuteDelete`, dropping the extra read round-trip — `CleanStaleAutomaticSegmentsAsync`, `ResetSeasonForReanalysisAsync`, `GetSeasonQueueSnapshotAsync`, `CleanSeasonStateAsync`, plus `CleanCacheTask`'s cache delete) now use `EF.Parameter`, each pinned by a 33,000-ID test (> 32,766). Residual risk — the annotation is per-query and a refactor could silently drop it — is contained by the pin tests, which fail with 'too many SQL variables' the moment `EF.Parameter` disappears. Small enum/mode collections (≤6 elements) keep the default multi-parameter translation for index-friendly plans. |
| `ExecuteUpdate` / `ExecuteDelete` | **Use (already in use)** | All bulk deletes go through `ExecuteDeleteAsync`. The EF10 non-expression `ExecuteUpdateAsync(setters => …)` overload was evaluated for the "clear `EpisodeIds`" writes; not adopted because those writes go through a value-converted property inside a transaction that also needs the loaded entities, so tracked updates are equally cheap and keep one code shape. Adopt where a standalone conditional bulk-update appears. |
| Compiled queries (`EF.CompileAsyncQuery`) | **Use, narrowly** | Adopted for exactly one query: `GetSegmentsAsync`, the per-playback hot path (`SegmentProvider.GetMediaSegments`). It removes per-call expression-tree allocation + query-cache lookup. Benefit is honest-but-small (the SQLite read dominates); cost is near-zero since the query is trivial. A cross-model hazard (compiled delegate vs. contexts built from different options) was considered and is empirically pinned by test `GetSegmentsAsync_CompiledQuery_WorksAcrossPathAndOptionsBasedContexts`. Not worth extending to cold paths. |
| Named query filters | **Don't use** | No global-filter use case: `IsUserProvided`/`ConfigHash` predicates vary per operation, and a filter default that silently hides rows is exactly how the user-precedence invariant would get corrupted. |
| `LeftJoin` operator | **Don't use** | Three entities, zero navigations, no joins anywhere in the workload; season-state + segments are fetched as two indexed lookups on purpose (snapshot shape). |
| Compiled models | **Don't use** (per shared brief) | 3-entity model; model build is a one-time startup cost measured in ms, already hidden behind the async init gate. |

---

## 8. Testing strategy

Answering the classic objection "no interface ⇒ nothing to mock": **this repo already
tests against real SQLite** (temp files / in-memory connections) and reflection-patched
`Plugin.Instance`. Theory C makes those tests *simpler*, not harder:

- Operations are extensions on the context, so tests call them directly on a temp-file
  context — no `Plugin.Instance`, no `FormatterServices.GetUninitializedObject`, no
  private-field reflection. Compare `TestDbOperations.UpdateTimestampAsync_CreditsOverlapGuard`
  (new, 0 reflection) with the equivalent in `TestDbSegmentStorage` (needs a fake plugin
  scope + 2 reflected fields).
- Consumers take `IDbContextFactory<T>`; tests hand them a 3-line fixed-path factory
  (`EntrypointTestHelpers.FixedPathIntroSkipperDbContextFactory`). Faking data is done by
  *seeding the database*, which is both easier and higher-fidelity than mocking a
  repository (unique indexes, converters, and transactions actually execute).
- New coverage added in the spike (`TestDbOperations`): user-provided precedence (2
  tests), credits/intro overlap guard (3 cases), 33,000-ID pin tests (> `SQLITE_MAX_VARIABLE_NUMBER`)
  for `CleanTimestampsAsync`, `CleanStaleAutomaticSegmentsAsync` (user rows preserved),
  `ResetSeasonForReanalysisAsync`, and `GetSeasonQueueSnapshotAsync`, compiled-query
  cross-context safety, the init-gate ordering proof, and cache-init fault isolation.
- All pre-existing DB tests (legacy repair, migration history, rebuild, the original
  33k-episode `CleanTimestampsAsync` test) pass unchanged through the delegating `Plugin`
  methods, demonstrating behavioral equivalence of the moved code.

What is genuinely harder: simulating *database failures* (e.g. "TryRead swallows
`DbException`"). A repository mock can `Throw()`; here you corrupt a real file or use an
interceptor. The existing suite already does the former for cache recovery, so no new
capability is lost — but pure-unit-test purists will not love it.

---

## 9. Risk register

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | **Invariant bypass**: a future call site does `db.DbSegment.Add(...)` directly, skipping user-precedence/overlap rules | Medium | High (silent data corruption of user edits) | Class-doc convention + review anchor; invariant tests pin behavior; documented upgrade path to BannedApiAnalyzers (`RS0030` already error-severity); schema-level unique indexes catch the duplicate class of mistakes regardless |
| 2 | **Query-logic drift/duplication** across inline call sites (e.g. two subtly different "erase season" flows) | Medium | Medium | The inline/shared line (§1) forces multi-statement flows into ops; inventory table names the two end-state ops (`EraseSeasonDataAsync`, `ClearEpisodesAsync`) precisely to prevent the known duplication between `EraseSeasonAsync` and `ClearExcludedTimestampsAsync` |
| 3 | Sync `CreateDbContext()` blocks on first call during a long migration; N concurrent sync callers pin N thread-pool threads | Low (migrations are small; hosted service usually wins the race) | Medium (slow requests at startup) | Round 2: audited — every async call site (delegators included) now uses `CreateDbContextAsync`; sync gate remains only for the genuinely-sync `IDetectionCacheService` surface; eager hosted kick-off; no synchronization context in Jellyfin ⇒ no deadlock |
| 4 | Compiled query executed against a context with a different model instance throws | Low (single context type ⇒ single cached model) | Medium | Pinned by dedicated test across both construction styles; fallback is deleting one `EF.CompileAsyncQuery` line |
| 9 | A refactor drops an `EF.Parameter` annotation, silently reverting to per-element parameters and re-exposing the 32766 limit | Low | High at pathological library sizes | 33,000-ID pin tests on every unbounded-collection operation fail immediately ('too many SQL variables') if the annotation disappears |
| 10 | Faulted init task poisoning all context creation (round-1 defect) | — (fixed) | High | Round 2: per-database gates + catch-all init cores; pinned by `CacheInitializationFailure_DoesNotBlockSegmentDatabaseAccess` |
| 5 | Transitional state: `Plugin` delegators + `Plugin.Instance` fallback path (`new IntroSkipperDbContext(DbPath)`) bypasses the gate for reflection-created test plugins | Certain (by design) | Low (test-only path; prod always has the factory) | Fallback exists only when the ctor never ran; end state deletes delegators and fallback entirely |
| 6 | `AddDbContextFactory` registers EF core services into Jellyfin's *global* container (options, scoped `TContext`) | Certain | Low | Registrations are generic over our context types only; no collision with Jellyfin's own EF registrations (verified: server boots these patterns for other plugins; nothing resolves a bare `DbContext`) |
| 7 | Threading the factory through 5 manually-`new`-ed constructors (end state) touches many signatures at once | Certain | Medium (mechanical but wide diff) | Constructors already thread 6–8 dependencies this way; change is type-checked end-to-end; can land per-consumer since delegators keep old paths alive |
| 8 | Migration-history/legacy-repair behavior must stay byte-identical for existing user DBs | — | High | Deliberately did **not** move or edit `EnsureLegacySchemaCompatibility`, `ApplyMigrations`, `RebuildDatabaseAsync`, or the design-time factory; only their *invocation point* moved (Plugin ctor → initializer). All migration/legacy tests pass unchanged |

---

## 10. Honest cons vs. the rival designs

Arguing against myself, ranked by how much they should worry the coordinator:

1. **No compile-time write seam.** A layered store can make invariant bypass *impossible*
   (private `DbSet` access, interface exposing only `UpdateTimestampAsync`). Theory C's
   protection is convention + analyzer-optional + tests. In a plugin with drive-by
   contributors, that's a real, recurring review burden — this is the strongest argument
   for a facade.
2. **The seam is EF-shaped forever.** Every consumer signature says
   `IDbContextFactory<IntroSkipperDbContext>`. If the storage story ever changes (Jellyfin
   shared DB, server-side segments API), the blast radius is every consumer, not one
   store implementation. Mitigating honesty: this plugin's contexts have survived years
   with the storage story only getting *more* SQLite, and the rival designs' abstraction
   would still leak EF types (`DbSegment`, transactions) unless they invest heavily.
3. **Extension-method ergonomics have warts.** Optional `ILogger?` parameters thread
   through operations that log (`UpdateTimestampAsync`); a store would own its logger.
   Discoverability via IntelliSense also means *everything* is discoverable — including
   raw `DbSegment` access sitting right next to the safe operations.
4. **Two-step call sites.** `create context → call operation` at every consumer vs. a
   store's one-liner. Multiplied across ~15 consumers this is visible noise; it's the tax
   for making context lifetime/transaction scope explicit and composable.
5. **Testing double standard.** "Real SQLite everywhere" is a feature until someone needs
   to unit-test a consumer's *error handling* without a real corrupt file. No seam to
   inject failures cheaply.

Where Theory C beats the rivals, for symmetry: zero new abstraction to maintain, the
smallest possible diff from current code (the 18 methods moved nearly verbatim), full EF
capability (transactions spanning operations, `ExecuteDelete`, compiled queries) with no
interface lag, and DI/lifecycle mechanics (`AddDbContextFactory`, init gate) that are
straight from the EF playbook rather than invented here.

---

## 11. Prototype map

| File | Role |
|---|---|
| `IntroSkipper/Db/IntroSkipperDatabase.cs` | Paths + shared SQLite options config (single source of truth) |
| `IntroSkipper/Db/DatabaseInitializer.cs` | Lifecycle owner: two independent never-faulting init gates, legacy repair + migrate, cache recovery, rebuild |
| `IntroSkipper/Db/DatabaseInitializationService.cs` | Hosted eager kick-off |
| `IntroSkipper/Db/GatedIntroSkipperDbContextFactory.cs`, `GatedDetectionCacheDbContextFactory.cs` | `IDbContextFactory<T>` impls awaiting the gate |
| `IntroSkipper/Db/SegmentOperations.cs` | Segment ops incl. invariant home + compiled hot-path query + `EF.Parameter` set-based deletes |
| `IntroSkipper/Db/SeasonStateOperations.cs` | Season-state ops incl. transactional reset + `EF.Parameter` snapshot/cleanup |
| `IntroSkipper/PluginServiceRegistrator.cs` | DI registrations (§3) |
| `IntroSkipper/Plugin.cs` | Zero query logic; 18 thin delegators (transitional), bootstrap removed |
| `IntroSkipper/Providers/SegmentProvider.cs` | Migrated: factory-injected reads/deletes |
| `IntroSkipper/Controllers/SkipIntroController.cs` | Migrated: invariant write path, inline delete, initializer-owned rebuild |
| `IntroSkipper/FFmpeg/DetectionCacheService.cs` | Migrated: all 5 sites off `Plugin.CreateCacheDbContext()` (sync factory path — sync interface) |
| `IntroSkipper/Controllers/VisualizationController.cs` | Migrated (R2): both factories injected, async context creation |
| `IntroSkipper/ScheduledTasks/CleanCacheTask.cs` | Migrated (R2): cache factory injected, `EF.Parameter` delete |
| `IntroSkipper.Tests/TestDbOperations.cs` | New layer tests: invariants, 33k `EF.Parameter` pins, compiled query, init-gate ordering, fault isolation |
| `IntroSkipper.Tests/EntrypointTestHelpers.cs` | Test factories (`FixedPathIntroSkipperDbContextFactory`, `PluginCacheDbContextFactory`, `CreateDatabaseInitializer`) |

---

## 12. Round 2 revisions

Changes made in response to coordinator review (all verified by the full suite: 379
passing, only the known environmental `TestSilenceDetection` failure remains).

1. **Fault containment (high) — fixed with both suggested remedies.**
   `DatabaseInitializer` now has **two independent gates**, one per database
   (`EnsureSegmentDatabaseInitialized[Async]` / `EnsureCacheDatabaseInitialized[Async]`),
   and each gated factory awaits only its own database's gate; additionally both init
   cores are **catch-all log-and-continue**, so an init task can never fault and cache
   an exception, and `DatabaseInitializationService.StartAsync` can never abort host
   startup. The failure-domain split now matches the reason the databases are separate
   files in the first place. Pinned by
   `CacheInitializationFailure_DoesNotBlockSegmentDatabaseAccess`, which injects a cache
   configuration that throws `InvalidOperationException` (outside the old
   `IOException or SqliteException` filter) and asserts the combined gate completes and
   the segment factory still hands out fully migrated contexts.

2. **`EF.Parameter` adopted; `Chunk(500)` removed (major, verdict revised).**
   The coordinator's benchmark on this exact stack (33k-ID `Contains` ×5: 235 ms vs
   1552 ms chunked; NOT-IN `ExecuteDelete` at 33k: 218 ms; default translation throws)
   outweighs round 1's provider-agnosticism argument. All unbounded-collection
   operations now pass the collection as a single JSON parameter:
   - `CleanTimestampsAsync` → one `ExecuteDelete` with `!EF.Parameter(ids).Contains(...)`
     (also drops the previous read-then-delete round-trip entirely);
   - `CleanStaleAutomaticSegmentsAsync`, `ResetSeasonForReanalysisAsync`,
     `GetSeasonQueueSnapshotAsync`, `CleanSeasonStateAsync` → `EF.Parameter` IN/NOT-IN;
   - `CleanCacheTask`'s stale-cache delete likewise; its stale-ID *read* was also fixed —
     the old `Where(id => !enabledIds.Contains(id))` against the queryable would itself
     have exceeded the parameter limit under EF10's default translation at 33k enabled
     episodes, and is now a full distinct-ID fetch + client-side `ExceptWith`.
   `SqliteParameterBatchSize` and all chunk loops are deleted. Each rewritten operation
   is pinned by a 33,000-ID test (> `SQLITE_MAX_VARIABLE_NUMBER` = 32,766) that fails
   with 'too many SQL variables' if the `EF.Parameter` annotation is ever dropped —
   converting the round-1 "annotation can silently disappear" objection into a caught
   regression. Small mode/enum collections (≤ 6 elements) intentionally keep the default
   translation. The original 33k `Plugin.CleanTimestampsAsync` regression test passes
   unchanged through the delegator.

3. **Sync-over-async gate (documented + mitigated).** Audit result: the 18 `Plugin`
   delegators now create contexts through a private async helper
   (`await factory.CreateDbContextAsync(ct)`), and `VisualizationController` (3 sites)
   and `CleanCacheTask` (2 sites) were migrated off `Plugin.Create*DbContext()` onto
   injected factories with async creation — eliminating every sync-from-async context
   creation in production code. The synchronous gate remains only behind the genuinely
   synchronous `IDetectionCacheService` surface (`DetectionCacheService`), whose
   interface is sync by contract. Risk register #3 updated. (Public transitional
   `Plugin.CreateDbContextAsync` variants were deliberately *not* added: CA1849 would
   then flag every remaining sync call in async test helpers, forcing unrelated test
   churn for API that the end state deletes anyway.)

4. **WAL (verified).** Round 1 made no WAL claim; §5 now records the verification: EF's
   SQLite provider sets `journal_mode=wal` when migrations create the database and the
   mode persists in the file (a fresh raw `SqliteConnection` on a migration-created DB
   reports `wal`), so the interceptor correctly leaves journal mode alone.

Net effect on the theory's standing: item 2 strengthens Theory C's core claim — staying
on the raw EF surface let the fix land as a one-line-per-query annotation with no
interface changes, and made the maintenance path measurably faster; item 1 was a real
design defect in round 1 (a facade would have had the same bug, but honesty requires
noting the gate design needed a second pass to get failure domains right).
