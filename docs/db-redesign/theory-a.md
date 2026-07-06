# Theory A — Repository/Store-per-Aggregate + Domain Service

Exploration spike for the intro-skipper database-layer rewrite (branch `12.0`, net10.0, EF Core 10.0.7).
This document describes the architecture, proves the prototype's key claims empirically, and — because
this is a comparison spike — documents the weaknesses honestly instead of hiding them.

**Prototype status:** compiling, StyleCop/analyzer-clean (`TreatWarningsAsErrors`), full test suite green
(375 passed / 1 known environmental failure `TestAudioFingerprinting.TestSilenceDetection`).

---

## 1. Architecture overview

```
                 ┌──────────────────────────────────────────────────────────────┐
                 │                     PluginServiceRegistrator                  │
                 │  (all singletons; hand-rolled IDbContextFactory<T> per DB)    │
                 └──────────────────────────────────────────────────────────────┘
   consumers                          domain                    persistence               lifecycle
┌────────────────┐        ┌───────────────────────┐      ┌──────────────────────┐   ┌──────────────────────┐
│ SegmentProvider│──────▶ │                       │      │ ISegmentStore        │   │ IDatabaseInitializer │
│ SkipIntroCtrl  │──────▶ │ ISegmentUpdateService │─────▶│  (DbSegment)         │──▶│  · segment gate      │
│ Analyzers*     │        │  · user-provided      │      ├──────────────────────┤   │    (Lazy<Task>)      │
│ BaseItemTask*  │──────▶ │    precedence         │      │ ISeasonStateStore    │──▶│  · cache gate        │
│ QueueManager*  │        │  · credits/intro      │      │  (DbSeasonState +    │   │    (Lazy<bool>)      │
│ CleanCacheTask │──────▶ │    overlap guard      │      │   2 cross-agg. ops)  │   │  · RebuildDatabase   │
│ SegmentEditor* │        │  · commercial dedupe  │      ├──────────────────────┤   │  · legacy repair     │
│ DetectionCache-│        └───────────────────────┘      │ IDetectionCacheStore │──▶│  · EnsureSchema      │
│ Service        │──────────────────────────────────────▶│  (DbDetectionCache,  │   └──────────┬───────────┘
└────────────────┘                                       │   introskipper-      │              │ warmed by
  * = still on transitional Plugin delegates, see §3     │   cache.db)          │   ┌──────────▼───────────┐
                                                         └──────────────────────┘   │ DatabaseStartupService│
                                                                                    │ (IHostedService)      │
                                                                                    └───────────────────────┘
```

### Components

| Component | Kind | Responsibility |
|---|---|---|
| `ISegmentStore` / `SegmentStore` | store (singleton) | All `DbSegment` persistence. Compiled query on the playback hot path. No business rules. |
| `ISeasonStateStore` / `SeasonStateStore` | store (singleton) | All `DbSeasonState` persistence **plus** the two operations that atomically span segments + season state (`ResetSeasonForReanalysisAsync`, `GetSeasonQueueSnapshotAsync`). See §8 for why. |
| `IDetectionCacheStore` / `DetectionCacheStore` | store (singleton) | All `introskipper-cache.db` persistence. Synchronous surface mirroring `IDetectionCacheService`. |
| `ISegmentUpdateService` / `SegmentUpdateService` | domain service | Business rules formerly inside `Plugin.UpdateTimestampAsync` / `DeleteTimestampAsync`: user-provided precedence, credits/intro overlap guard, commercial epsilon-dedupe, "no blind commercial delete". |
| `IDatabaseInitializer` / `DatabaseInitializer` | lifecycle | Legacy schema repair + EF migrations (segment DB), `EnsureSchema` with delete-and-recreate recovery (cache DB), `RebuildDatabaseAsync`. Exposes the async init **gate** every store awaits. |
| `DatabaseStartupService` | `IHostedService` | Eagerly warms both gates at server startup. Optimization only — correctness never depends on it (§5). |
| `SegmentDbContextFactory` / `DetectionCacheDbContextFactory` | infrastructure | Hand-rolled `IDbContextFactory<T>` singletons; options built once per factory; shared static `SqlitePragmaInterceptor.Instance`. |
| `PluginDatabasePaths` | infrastructure | Single source of truth for DB paths, computed from `IApplicationPaths`; used by both DI registrations and the `Plugin` constructor so they cannot diverge. |

### Rationale for this shape

- **Stores are pure persistence.** Every store method is "open context → query/mutate → dispose". The single
  place where a domain decision must be *transactionally atomic* with a write
  (`UpdateTimestampAsync`'s read-check-write) is handled by a callback primitive:
  `ISegmentStore.ReplaceNonCommercialAsync(segment, Func<NonCommercialSegmentContext, bool> shouldPersist)`.
  The store owns the transaction and loads the snapshot (existing rows + stored intro); the domain service
  owns the decision. This preserves the exact atomicity of the original implementation (BEGIN → read →
  decide → delete+insert → COMMIT) without moving rules into the store or a context factory into the service.
- **The domain service is deliberately small.** Only rules that currently live in `Plugin` moved into it.
  No speculative "domain model" was invented — `DbSegment`/`Segment` stay as they are.
- **Interface granularity — considered and consolidated.** An earlier sketch had five interfaces
  (`ISegmentReader`/`ISegmentWriter` splits, a separate `ISeasonSnapshotReader`). That is over-engineering
  for a 3-entity plugin; it was collapsed to exactly one store per aggregate (segment, season-state, cache)
  plus one domain service. I did **not** collapse further into a single facade because the two databases have
  different lifecycles (migrations vs. ensure-created) and different consumers, and because the store-per-aggregate
  seam is the point of Theory A — but §9 states plainly what a facade would have saved.

---

## 2. Complete migration inventory

### 2.1 The 18 `Plugin` DB methods

"Prototype" column: **migrated** = implementation lives in the new layer, `Plugin` method is a one-line
delegate kept only for manually-constructed callers; end state deletes the `Plugin` method entirely.

| # | Current `Plugin` member | New home | Notes | Prototype |
|---|---|---|---|---|
| 1 | `UpdateTimestampAsync` | `SegmentUpdateService.UpdateTimestampAsync` → `SegmentStore.TryAddCommercialAsync` / `SegmentStore.ReplaceNonCommercialAsync` | Rules in service; decision runs inside store transaction via callback | migrated |
| 2 | `GetTimestampsAsync` | `SegmentStore.GetTimestampsAsync` | Groups over the compiled segment query | migrated |
| 3 | `GetSegmentsAsync` | `SegmentStore.GetSegmentsAsync` | `EF.CompileAsyncQuery` hot path (playback) | migrated |
| 4 | `DeleteItemSegmentsAsync` | `SegmentStore.DeleteSegmentsAsync(Guid)` | `ExecuteDeleteAsync` | migrated |
| 5 | `CleanTimestampsAsync` | `SegmentStore.CleanTimestampsAsync` | **Chunk(500) removed**: single server-side `DELETE … WHERE ItemId NOT IN (json_each(@ids))` via `EF.Parameter` (§6.2). Replaces read-all-distinct + client diff + batched deletes | migrated |
| 6 | `SetAnalyzerActionAsync` | `SeasonStateStore.SetAnalyzerActionsAsync` | body ported verbatim | migrated |
| 7 | `SetEpisodeIdsAsync` | `SeasonStateStore.SetEpisodeIdsAsync` | body ported verbatim | migrated |
| 8 | `RemoveEpisodeIdAsync` | `SeasonStateStore.RemoveEpisodeIdAsync` | read+write share one context (unchanged) | migrated |
| 9 | `CleanStaleAutomaticSegmentsAsync` | `SegmentStore.CleanStaleAutomaticSegmentsAsync` | Chunk(500) removed via `EF.Parameter` | migrated |
| 10 | `GetEpisodeIdsAsync` | `SeasonStateStore.GetEpisodeIdsAsync` | | migrated |
| 11 | `GetSettleReanalysisStatesAsync` | `SeasonStateStore.GetSettleReanalysisStatesAsync` | | migrated |
| 12 | `RecordSettleReanalysisAsync` | `SeasonStateStore.RecordSettleReanalysisAsync` | raw upsert SQL unchanged | migrated |
| 13 | `ResetSeasonForReanalysisAsync` | `SeasonStateStore.ResetSeasonForReanalysisAsync` | cross-aggregate transaction (segments + season state); Chunk removed via `EF.Parameter` inside the transaction | migrated |
| 14 | `GetSeasonQueueSnapshotAsync` | `SeasonStateStore.GetSeasonQueueSnapshotAsync` | cross-aggregate read; episode batching removed via `EF.Parameter`; `SeasonQueueSnapshot` made public | migrated |
| 15 | `GetAllAnalyzerActionsAsync` | `SeasonStateStore.GetAllAnalyzerActionsAsync` | | migrated |
| 16 | `GetAnalyzerActionAsync` | `SeasonStateStore.GetAnalyzerActionAsync` | | migrated |
| 17 | `CleanSeasonStateAsync` | `SeasonStateStore.CleanSeasonStatesAsync` | `EF.Parameter` (the old inline `!ids.Contains(...)` would break at >32 766 seasons under EF 10's default translation) | migrated |
| 18 | `DeleteTimestampAsync` | `SegmentUpdateService.DeleteTimestampAsync` → `SegmentStore.DeleteSegmentsAsync(Guid, AnalysisMode, Segment?, double)` | "commercial requires explicit match" rule stays in service | migrated |

Also removed from `Plugin` in the end state: `CreateDbContext()` / `CreateCacheDbContext()` statics,
`SegmentComparisonEpsilon` (now `SegmentUpdateService.SegmentComparisonEpsilon`), `SqliteParameterBatchSize`
(obsolete). `ShouldSettleReanalyze` (pure function, no DB) and `MapSegmentTypeToMode` stay.

### 2.2 Direct-context call sites

| Site | Current | New | Prototype |
|---|---|---|---|
| `SkipIntroController.ResetIntroTimestamps` | `Plugin.CreateDbContext()` + `ExecuteDeleteAsync` | `ISegmentStore.DeleteSegmentsByTypeAsync(mode)` (injected) | **migrated** |
| `SkipIntroController.RebuildDatabase` | `Plugin.CreateDbContext()` + `db.RebuildDatabaseAsync(Plugin.CreateDbContext)` | `IDatabaseInitializer.RebuildSegmentDatabaseAsync()` (injected) | **migrated** |
| `VisualizationController.EraseSeasonAsync` | `Plugin.CreateDbContext()` (segment delete + season-state clear) | end state: `ISeasonStateStore.EraseSeasonAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds)` — new composite method so the segment delete and episode-list clear stay on one connection | table-documented, not yet migrated |
| `VisualizationController.ClearExcludedTimestampsAsync` (segment DB) | `Plugin.CreateDbContext()` | end state: `ISegmentStore.DeleteSegmentsForItemsAsync(IReadOnlyCollection<Guid>) : Task<int>` + `ISeasonStateStore.RemoveEpisodesFromSeasonsAsync(IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>)` | table-documented, not yet migrated |
| `VisualizationController.ClearExcludedTimestampsAsync` (cache DB) | `Plugin.CreateCacheDbContext()` | `IDetectionCacheStore.DeleteForItemsAsync(ids)` (already exists) | table-documented, not yet migrated |
| `CleanCacheTask` (cache read of distinct IDs) | `Plugin.CreateCacheDbContext()` | `IDetectionCacheStore.GetItemIdsAsync()` | **migrated** |
| `CleanCacheTask` (cache batched delete) | `Plugin.CreateCacheDbContext()` | `IDetectionCacheStore.DeleteForItemsAsync(ids)` | **migrated** |
| `DetectionCacheService.TryRead` | `Plugin.CreateCacheDbContext()` | `IDetectionCacheStore.Find(...)` | **migrated** |
| `DetectionCacheService.Write` | `Plugin.CreateCacheDbContext()` | `IDetectionCacheStore.Upsert(...)` | **migrated** |
| `DetectionCacheService.DeleteForItem` | `Plugin.CreateCacheDbContext()` | `IDetectionCacheStore.DeleteForItem(...)` | **migrated** |
| `DetectionCacheService.DeleteByMode` | `Plugin.CreateCacheDbContext()` | `IDetectionCacheStore.DeleteByMode(...)` | **migrated** |
| `DetectionCacheService.HasCachedFingerprint` | `Plugin.CreateCacheDbContext()` | `IDetectionCacheStore.Exists(...)` | **migrated** |
| `Plugin` ctor (migrations + legacy repair + cache `EnsureSchema`) | inline in ctor | `IDatabaseInitializer` + `DatabaseStartupService` | **implemented**; ctor init retained transitionally (§5) |

`DetectionCacheService` no longer references `Plugin.CreateCacheDbContext()` anywhere — the required
slice item is complete. Its remaining `Plugin.Instance` reads are **configuration** access
(`CacheFingerprints`, compression level, config hashing), which is out of scope for the DB rewrite.

### 2.3 Threading dependencies through manually-constructed types (end state)

These types are `new`-ed, not DI-resolved, so the end state passes stores down explicitly (no
`Plugin.Instance` for DB work):

| Type | Constructed by | New ctor parameters |
|---|---|---|
| `BaseItemAnalyzerTask` | `Entrypoint`, `VisualizationController.ScanSeason`, `DetectSegmentsTask` (all DI contexts) | `ISegmentUpdateService`, `ISegmentStore`, `ISeasonStateStore` |
| `QueueManager` | `CleanCacheTask`, `VisualizationController`, `BaseItemAnalyzerTask` (all DI contexts or already-threaded) | `ISeasonStateStore` (for `GetSeasonQueueSnapshotAsync`) |
| `ChromaprintAnalyzer`, `ChapterAnalyzer`, `BlackFrameAnalyzer`, `CreditsBlackFrameAnalyzer` | `BaseItemAnalyzerTask` | `ISegmentUpdateService` |
| `RecapDetectionHelper` | analyzers | `ISegmentStore` (for `GetTimestampsAsync`) |
| `SegmentEditorController` | DI (controller) | inject `ISegmentStore`, `ISegmentUpdateService`, `ISeasonStateStore` |

Every root of these object graphs is DI-resolved, so the dependencies flow down without any service
locator. The prototype keeps them on the one-line `Plugin` delegates to bound the diff; the delegates
already forward into the exact same store code paths, so switching a caller is mechanical.

---

## 3. DI registration (as implemented)

```csharp
// PluginServiceRegistrator.RegisterServices
serviceCollection.AddSingleton<IDbContextFactory<IntroSkipperDbContext>>(sp =>
    new SegmentDbContextFactory(PluginDatabasePaths.GetSegmentDbPath(sp.GetRequiredService<IApplicationPaths>())));
serviceCollection.AddSingleton<IDbContextFactory<DetectionCacheDbContext>>(sp =>
    new DetectionCacheDbContextFactory(PluginDatabasePaths.GetCacheDbPath(sp.GetRequiredService<IApplicationPaths>())));
serviceCollection.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
serviceCollection.AddSingleton<ISegmentStore>(sp => new SegmentStore(
    sp.GetRequiredService<IDbContextFactory<IntroSkipperDbContext>>(),
    sp.GetRequiredService<IDatabaseInitializer>()));
serviceCollection.AddSingleton<ISeasonStateStore>(sp => new SeasonStateStore(
    sp.GetRequiredService<IDbContextFactory<IntroSkipperDbContext>>(),
    sp.GetRequiredService<IDatabaseInitializer>()));
serviceCollection.AddSingleton<IDetectionCacheStore>(sp => new DetectionCacheStore(
    sp.GetRequiredService<IDbContextFactory<DetectionCacheDbContext>>(),
    sp.GetRequiredService<IDatabaseInitializer>()));
serviceCollection.AddSingleton<ISegmentUpdateService>(sp => new SegmentUpdateService(
    sp.GetRequiredService<ISegmentStore>(),
    sp.GetRequiredService<ILogger<SegmentUpdateService>>()));

// Registered before Entrypoint so database initialization starts first.
serviceCollection.AddHostedService<DatabaseStartupService>();
serviceCollection.AddHostedService<Entrypoint>();
```

Paths come from `IApplicationPaths` (DI-resolvable at registration time), **not** `Plugin.Instance`, so the
data layer has zero dependency on plugin construction order. `PluginDatabasePaths` is also used by the
`Plugin` constructor, so the transitional delegates and the DI singletons always agree on the same files.

---

## 4. Why hand-rolled `IDbContextFactory<T>` instead of `AddDbContextFactory` (the pooling verdict)

This is the first EF-10-checklist verdict because it shapes everything else; see §6 for the rest.

`AddDbContextFactory`/`AddPooledDbContextFactory` call EF's `AddCoreServices`, which `TryAdd`s ~60 EF
internal service registrations **into Jellyfin's shared service collection**. Jellyfin server registers its
own EF Core services for `jellyfin.db`, and plugin assemblies load in a separate `AssemblyLoadContext` — so
whether the plugin's `TryAdd`s win or lose depends on registration order and on whether the server's EF
assembly identity matches the plugin's EF 10.0.7. That is exactly the kind of cross-version fragility a
plugin must not introduce. The hand-rolled factories:

```csharp
internal sealed class SegmentDbContextFactory : IDbContextFactory<IntroSkipperDbContext>
{
    private readonly DbContextOptions<IntroSkipperDbContext> _options; // built once
    public IntroSkipperDbContext CreateDbContext() => new(_options);
}
```

keep every EF service private to the contexts, while still exposing the *standard* `IDbContextFactory<T>`
abstraction — swapping to EF's factory later is a one-line DI change, invisible to the stores.

**Pooling (`AddPooledDbContextFactory`) — rejected**, for three independent reasons:

1. **Constructor constraint.** Pooling requires the context to expose a single public
   `DbContextOptions`-based constructor. `IntroSkipperDbContext` deliberately keeps the legacy
   string-path constructor: it is used by `RebuildDatabaseAsync`'s `DeleteDatabaseFiles` fallback
   (`_dbPath` field), by the design-time flow, and by ~30 existing tests. Reconciling it means either
   deleting that ctor (large test churn, riskier rebuild path relying purely on connection-string parsing)
   or maintaining two context types.
2. **`OnConfiguring` semantics.** Pooled instances are created once and reset on return; per-instance
   `OnConfiguring`-based configuration (what the string-path ctor relies on) does not participate per
   rental. With non-pooled factory-created contexts, options are baked into the factory and
   `OnConfiguring` short-circuits on `IsConfigured` — the current behavior, preserved.
3. **No measurable benefit at this workload.** The hottest path is one small indexed query per playback
   start; analysis is a batch job dominated by FFmpeg. `DbContext` construction is microseconds against
   SQLite I/O milliseconds. Pooling's win (avoiding context+service scope allocation at thousands of
   req/s) simply does not apply. Interceptor interplay is a non-issue either way because
   `SqlitePragmaInterceptor` is registered in options (singleton, stateless) — but with pooling one must
   additionally verify connection-state resets between rentals; not worth the audit for zero gain.

One subtlety the prototype handles: EF caches its internal service provider keyed by options fingerprints.
All factories share the single static `SqlitePragmaInterceptor.Instance` and build options once, so the
whole plugin still resolves to **one** cached EF internal provider (same as today), avoiding
`ManyServiceProvidersCreatedWarning` even while the transitional `Plugin` delegates construct short-lived
factories.

---

## 5. Initialization & lifecycle design (with ordering proof)

**Chosen option:** async init gate *inside the data layer* + eager `IHostedService` warm-up.
(Rejected: "keep bootstrap in Plugin ctor delegating to the new layer" — it would force sync-over-async
into the ctor and keeps `Plugin` in the lifecycle business; "hosted-service-only initialization" — unsafe
alone, because `IMediaSegmentProvider`/controllers can theoretically be hit while startup is racing.)

Mechanism (`DatabaseInitializer`):

- Segment DB: `Lazy<Task>` (`ExecutionAndPublication`) running `EnsureLegacySchemaCompatibility()` then
  `Database.MigrateAsync()`. `EnsureSegmentDbReadyAsync(ct)` returns `_lazy.Value.WaitAsync(ct)` — a
  cancelled caller abandons its *wait*, never the shared initialization, so one impatient request can't
  poison the gate.
- Cache DB: `Lazy<bool>` running `EnsureSchema()` (EnsureCreated + probe + delete-and-recreate recovery),
  synchronous because the whole cache surface is synchronous.
- Failure policy: parity with the current `Plugin` ctor — log and continue (a genuinely broken DB then
  fails per-query with actionable errors). Caching a *faulted* task instead would turn one transient init
  failure into a permanently dead plugin, which is worse than today's behavior.
- `RebuildSegmentDatabaseAsync` first awaits the gate (a rebuild must never interleave with first-time
  migration), then runs the existing `IntroSkipperDbContext.RebuildDatabaseAsync` salvage flow with the
  factory as the sibling-context source. The salvage/`forceCleanOnBackupFailure` logic itself is untouched.

**Ordering proof.** Claim: no query observes an unmigrated database.

1. In the end state, *every* database access is inside a store method (inventory §2 is exhaustive; the
   context types stop being constructible outside `Db/` once the `Plugin` statics are deleted).
2. Every store method's first awaited action is `EnsureSegmentDbReadyAsync` (or `EnsureCacheDbReady` for
   the cache store) — checked structurally: the private `CreateContextAsync`/`CreateContext` helpers are
   the only way store code obtains a context.
3. `Lazy<T>` with `ExecutionAndPublication` guarantees the init body runs at most once and that every
   caller's continuation runs only after the shared task completes. Therefore any store call either
   triggers initialization and waits, or waits on the already-running/completed task. ∎
4. `DatabaseStartupService` merely *warms* the gate before Jellyfin serves traffic (ASP.NET Core starts
   hosted services before the server accepts requests; it is registered before `Entrypoint`). If that
   assumption ever breaks, correctness is unaffected — only first-call latency.
5. Concurrency corner: two concurrent cold-start callers → one migration run (proven by the
   `DatabaseInitializer_GatesStoreAccess_UntilMigrationsComplete` test, which fires 8 parallel store calls
   at a nonexistent database file and asserts success + zero pending migrations).

**Transitional note (prototype):** the `Plugin` ctor still performs its synchronous init because
`VisualizationController` still uses `Plugin.CreateDbContext()` and tests construct contexts by path.
Double initialization is safe — `EnsureLegacySchemaCompatibility` and `Migrate` are idempotent, and the
plugin is constructed before hosted services start, so the two never race in practice. The end state
deletes the ctor block; the gate then becomes the *only* initialization path.

**Migration-history compatibility (invariant g):** untouched. `EnsureLegacySchemaCompatibility`,
`ApplyMigrations`, the `Migrations/` folder and `IntroSkipperDbContextFactory` (design-time) are all
byte-for-byte unchanged — only the *call site* moved from the `Plugin` ctor into `DatabaseInitializer`
(and the ctor retains it transitionally). Existing user DBs upgrade exactly as before;
`dotnet ef migrations` still uses the untouched design-time factory.

---

## 6. EF Core 10 feature verdicts

All verdicts were verified against EF Core 10.0.7 + `SQLitePCLRaw.lib.e_sqlite3` 3.50.3 (SQLite 3.50.3)
with a scratch probe project on this VM; the load-bearing ones are additionally locked in by unit tests.

### 6.1 `AddPooledDbContextFactory` vs `AddDbContextFactory`
**Verdict: neither — hand-rolled `IDbContextFactory<T>` singletons.** Full reasoning in §4.

### 6.2 EF 10 parameterized-collection translation vs manual `Chunk(500)`
**Verdict: manual chunking is removed, but only because every unbounded collection predicate is pinned to
`EF.Parameter(...)`; EF 10's *default* translation is NOT safe on SQLite for large sets.**

Measured on this repo's exact package versions:

| Translation | 8 elements | 33 000 elements |
|---|---|---|
| default (EF 10 = one scalar parameter per value, padded — e.g. 8 values → 10 params) | `WHERE "ItemId" IN (@p1..@p10)` ✅ | ❌ `SQLite Error 1: 'too many SQL variables'` |
| `EF.Parameter(ids).Contains(...)` (single JSON array parameter) | `IN (SELECT value FROM json_each(@ids))` ✅ | ✅ incl. `ExecuteDelete` with `NOT IN` |

Facts: e_sqlite3 3.50 has `SQLITE_MAX_VARIABLE_NUMBER = 32766` (999 only for SQLite < 3.32, which
`Microsoft.Data.Sqlite`'s bundled engine never is), so the historical `Chunk(500)` was ~65× more
conservative than needed — but under EF 10's new default it protects against a *real* failure mode again.
Policy adopted:

- **Unbounded ID sets** (`CleanTimestampsAsync`, `CleanStaleAutomaticSegmentsAsync`,
  `CleanSeasonStatesAsync`, `ResetSeasonForReanalysisAsync`, `GetSeasonQueueSnapshotAsync`,
  `DetectionCacheStore.DeleteForItemsAsync`): `EF.Parameter` → single JSON parameter, no limit, one
  round-trip. `CleanTimestampsAsync` collapsed from *read all distinct IDs + client-side diff + N batched
  deletes* into one server-side `DELETE … WHERE ItemId NOT IN (…)`.
- **Small bounded sets** (`modeArray.Contains(s.Type)`, ≤ 5 analysis modes): keep the EF 10 default
  multi-parameter translation — it gives the planner real cardinality and pads to stable bucket sizes.
- Locked in by tests at 33 000 IDs (> 32 766) against both databases, plus the pre-existing 33k
  `Plugin.CleanTimestampsAsync` test which now routes through the store.
- Residual risk & mitigation in §10 (R1).

### 6.3 `ExecuteUpdate` / `ExecuteDelete` (incl. EF 10 non-expression overload)
**Verdict: `ExecuteDeleteAsync` everywhere it applies (kept and extended); `ExecuteUpdate` — no use.**
Every bulk delete in the layer is a set-based `ExecuteDelete`. The only bulk-update candidates
(`SetEpisodeIdsAsync`, `RemoveEpisodeIdAsync`, `ResetSeasonForReanalysisAsync`'s list clear) mutate
`EpisodeIds`, a JSON-serialized value-converted collection that requires read-modify-write in memory —
`ExecuteUpdate` cannot express "remove one element from a JSON list" portably, and the row counts (a
handful of season rows) make tracked updates the simpler, equally-fast choice. The EF 10 non-expression
lambda overload would only matter for conditionally-composed `SetProperty` chains, which do not occur here.

### 6.4 Compiled queries (`EF.CompileAsyncQuery`)
**Verdict: use exactly once — the playback hot path.** `SegmentStore.GetSegmentsAsync` (called by
`SegmentProvider.GetMediaSegments` on every playback and reused by `GetTimestampsAsync`) uses a static
compiled query. It skips per-call expression-tree construction and query-cache lookup; with factory-created
contexts sharing one options/model instance the compiled plan is reused across contexts. Gains are real but
small (µs); spreading compiled queries over cold paths (scan-time, admin endpoints) would add boilerplate
for nothing, so we don't.

### 6.5 Named query filters
**Verdict: don't use.** The model has no soft-delete/tenant axis. The tempting candidate —
a global `IsUserProvided` filter — would *hide* the precedence rule inside model configuration where the
domain service can't reason about it (the rule needs to *see* user rows, not filter them out).

### 6.6 `LeftJoin` operator
**Verdict: don't use.** The three tables are deliberately unrelated (no navigations, no FKs); correlation
(`GetSeasonQueueSnapshotAsync`) happens in memory over two indexed reads, which is both simpler and avoids
a fan-out join over the JSON-converted columns. Nothing in the inventory becomes clearer as a `LeftJoin`.

### 6.7 Compiled models
**Verdict: don't use** (per shared guidance): 3 entities / 2 contexts; model building is sub-millisecond at
startup and happens once. Compiled models would add a generation step to the build for unmeasurable gain.

---

## 7. Transaction & SQLite concurrency strategy

- **Connections:** every store operation is a short-lived context → one pooled `Microsoft.Data.Sqlite`
  connection per operation, `busy_timeout=5000` applied on every open by the shared
  `SqlitePragmaInterceptor` (unchanged). WAL is EF's SQLite default (unchanged). Both stores over
  `introskipper.db` share the same factory, hence the same connection pool and pragmas.
- **Explicit transactions only where multi-statement atomicity is required**, exactly mirroring today:
  `ReplaceNonCommercialAsync` (read snapshot → domain decision → delete+insert) and
  `ResetSeasonForReanalysisAsync` (segment delete + episode-list clear). Everything else is a single
  statement (`ExecuteDelete`, single `SaveChanges`) riding SQLite's implicit transaction.
- **Write races:** SQLite serializes writers; `busy_timeout` absorbs contention between the analysis task,
  controllers and the cache. The domain-decision-inside-transaction design means the user-provided
  precedence check cannot be defeated by a concurrent analyzer write (same guarantee as the current code,
  now structurally enforced by the store primitive instead of by discipline inside one 80-line method).
- **Cross-database consistency** (e.g. erase segments then cache): not transactional today, not made
  transactional — the cache is derived data and self-heals; keeping the DBs in separate files (corruption
  isolation) is the higher-value property.
- **Rebuild:** `RebuildSegmentDatabaseAsync` awaits the init gate, then reuses the existing salvage flow
  including `SqliteConnection.ClearAllPools()` before file deletion.

## 8. Where the theory bent: cross-aggregate operations

`ResetSeasonForReanalysisAsync` (transactional) and `GetSeasonQueueSnapshotAsync` (consistent read) touch
both `DbSegment` and `DbSeasonState`. Splitting them across two stores would require either distributed
coordination between stores sharing a context (a unit-of-work abstraction — heavy) or losing atomicity
(a real invariant regression). Decision: the **season is the aggregate root** for analysis bookkeeping and
owns these two operations, at the acknowledged cost of `SeasonStateStore` querying `DbSegment`. The same
reasoning will apply to `VisualizationController.EraseSeasonAsync` when it migrates (§2.2). This is a
known, honest dent in repository purity — a facade design wouldn't even notice the problem, which is a
point in the facade's favor (§9).

## 9. Honest cons vs a simpler facade / no-repository design

- **File and interface count.** ~10 new types for 3 entities. A single `IIntroSkipperData` facade (one
  interface, one implementation, same initializer) would deliver the identical testability and DI story
  with a third of the surface. Theory A's extra seams only pay off if the persistence of one aggregate is
  ever swapped or decorated independently — there is no current roadmap item that needs that.
- **The abstraction is mostly 1:1 with use cases.** Nearly every store method has exactly one production
  caller. That's a facade wearing repository clothes; the "reusable persistence vocabulary" argument for
  repositories is weak here.
- **Purity broke immediately** (§8): two cross-aggregate operations and a callback-in-transaction
  primitive were needed on day one. `ReplaceNonCommercialAsync(segment, shouldPersist)` keeps the layering
  honest but is subtle — a contributor could add I/O inside the callback and unknowingly extend a write
  transaction. (Mitigated by doc contract + the callback receiving an immutable record, but it's a trap a
  facade doesn't have: the facade would just keep the 30-line method intact in one place.)
- **Transitional double surface.** Until analyzers/QueueManager/BaseItemAnalyzerTask are re-threaded, the
  18 `Plugin` delegates coexist with the stores. The delegates are one-liners into the same code, but a
  reviewer must still know which layer to call. A facade migration could have moved `Plugin` callers and
  implementation in a single step per method with less indirection.
- **Interface evolution tax.** Each future DB feature now costs interface + implementation + (often)
  domain-service touch, and the store interfaces are `public` (Jellyfin controllers must be public and
  their ctor parameter types with them), so their surface is effectively semi-API.
- Where Theory A genuinely wins: the domain rules are now unit-tested in isolation at two levels, the
  initializer is an explicit, testable component instead of ctor side effects, `SegmentProvider` and
  `SkipIntroController` are constructible in tests without reflection-based `Plugin` scopes, and the
  concurrency contract of the segment-write rules is enforced by construction.

## 10. Risk register

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | A future EF release changes `EF.Parameter` collection translation on SQLite (e.g. away from `json_each`), reintroducing parameter limits | low | high (silent until a big library hits it) | Three tests pin the behavior at 33 000 IDs (> 32 766) across both DBs; any regression fails CI loudly. Fallback is reintroducing `Chunk` inside the affected store methods only — call sites don't change. |
| R2 | DI-computed DB path diverges from the `Plugin` ctor path during the transition (two data directories) | very low | high | Single source of truth `PluginDatabasePaths` used by both; no string literals remain at either site. |
| R3 | EF internal service-provider cache bloat (`ManyServiceProvidersCreated`) from transitional per-call factories in `Plugin` delegates | low | medium | Shared static interceptor + identical options per path ⇒ one cached provider; removed entirely with the delegates in the end state. |
| R4 | Hosted-service start order changes in a future Jellyfin release (warm-up runs late) | low | none (perf only) | Correctness never depends on the warm-up — every store call awaits the gate itself (§5 proof). |
| R5 | Init failure semantics: gate swallows the error (parity with today), so a broken DB surfaces as per-query failures rather than one startup error | medium (unchanged from today) | medium | Same warning logs as today; `RebuildDatabase` endpoint remains the documented recovery path and now flows through the initializer, which serializes it against first-time init. |
| R6 | Copy-paste drift while porting 18 method bodies into stores | medium | high | Bodies ported verbatim (diff-reviewable); the untouched pre-existing test suite (367 tests incl. legacy-schema, salvage, 33k-parameter and overlap-guard tests) passed unmodified against the delegating layer before any new tests were added. |
| R7 | `shouldPersist` callback misuse (I/O inside the write transaction) | medium | medium | Contract documented on the interface; callback receives an immutable `NonCommercialSegmentContext`; only one implementer (`SegmentUpdateService`). If misuse recurs, replace with a closed enum-decision API at the cost of rule locality. |
| R8 | Public store interfaces become de-facto API for other plugins | low | low | Implementations stay `internal sealed`; interfaces documented as unstable spike surface. |
| R9 | Tests constructing `Plugin` via `GetUninitializedObject` bypass ctor init; transitional delegates must build stores from `_dbPath` per call | certain (test-only) | low | Handled: delegates resolve `_dbPath` lazily per call; disappears with the delegates. |

## 11. Testing strategy

- **Regression net first:** the entire pre-existing suite (including `UpdateTimestampAsync_CreditsOverlapGuard`,
  the 33k `CleanTimestampsAsync`/`GetSeasonQueueSnapshotAsync` parameter-limit tests, legacy-schema upgrade
  and salvage tests) ran **unmodified** against the delegating layer and passed — proving behavior parity of
  the ported implementations before any new tests were written.
- **New layer tests** (`TestSegmentStores.cs`, temp-file SQLite like the existing suite):
  - user-provided precedence end-to-end through `SegmentUpdateService` (auto never overwrites user; user
    overwrites user);
  - credits/intro overlap guard (same 3-case theory as the legacy test, now against the service);
  - commercial epsilon-dedupe (invariant c, multiples allowed);
  - `CleanTimestampsAsync` at 33 000 IDs directly against `SegmentStore` — the chunk-removal proof;
  - `DetectionCacheStore.DeleteForItemsAsync` at 33 000 IDs — same proof on the cache DB;
  - initializer gate: 8 concurrent store calls against a nonexistent DB file → all succeed, migrations
    applied exactly once, zero pending migrations (ordering proof, executable form).
- **Test seams gained:** `SkipIntroController` and `DetectionCacheService` are now constructed in tests
  from explicit stores/factories; the reflection-based `PluginInstanceScope` remains only for the
  transitional `Plugin` delegates and configuration access.
- Result: 375 passed / 1 failed — the known environmental `TestAudioFingerprinting.TestSilenceDetection`.

## 12. Invariant checklist (a–g)

| Invariant | Status |
|---|---|
| (a) user-provided segments never overwritten | Enforced in `SegmentUpdateService.ShouldPersist`, executed inside the store write transaction; tested at service level and via legacy tests. |
| (b) credits/intro overlap guard | Same mechanism; 3-case theory tests at both levels. |
| (c) commercial multiples + filtered unique indexes | Model config untouched; epsilon-dedupe in `TryAddCommercialAsync`; legacy index tests pass. |
| (d) `RebuildDatabaseAsync` salvage + `EnsureLegacySchemaCompatibility` | Code untouched; now invoked via `IDatabaseInitializer` (rebuild serialized behind the init gate); legacy tests pass. |
| (e) SQLite parameter-limit safety | Strengthened: `EF.Parameter` single-JSON-parameter, proven at 33k (> new-default failure threshold); old `Chunk(500)` removed. |
| (f) design-time factory / `dotnet ef migrations` | `IntroSkipperDbContextFactory` byte-for-byte untouched. |
| (g) migration history behavior | `Migrations/`, `_currentMigrationIds`, history-repair SQL all untouched; only the invocation site moved. |

## 13. Verdict summary

**Strongest argument for:** the two safety-critical rules (user precedence, overlap guard) moved from the
middle of an 80-line `Plugin` method into a 15-line, independently unit-tested domain service — while
*keeping* their transactional atomicity via the callback-in-transaction store primitive — and the
initialization/ordering problem became a provable, testable gate instead of constructor side effects.

**Strongest argument against:** for a plugin with 3 entities and ~20 call sites, store-per-aggregate is
near the over-engineering line — purity bent on day one (cross-aggregate season ops, callback primitive),
and a single facade would have bought ~80 % of the benefit (DI, gate, testability, EF10 wins) for ~35 % of
the surface area and none of the interface-evolution tax.
