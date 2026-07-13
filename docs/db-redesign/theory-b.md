# ADR: One Cohesive Database Facade per SQLite Database

Status: **accepted, implemented** (intro-skipper branch `12.0` database-layer redesign).

## 1. Architecture

Two sealed singleton facades, one per SQLite database, each closed over a DI-provided
`IDbContextFactory<TContext>`:

| Facade | Database | Context | Concerns |
|---|---|---|---|
| `IIntroSkipperDatabase` / `IntroSkipperDatabase` | `introskipper.db` | `IntroSkipperDbContext` | segments, season state, maintenance, lifecycle (legacy repair + migrations + rebuild) |
| `IDetectionCacheDatabase` / `DetectionCacheDatabase` | `introskipper-cache.db` | `DetectionCacheDbContext` | cache CRUD, stale-ID computation, lifecycle (`EnsureCreated` + corruption recovery) |

The facades own **all** database access: no caller sees a `DbContext`, and the two
write-path invariants (user-provided segments are never overwritten by analysis
results; auto-detected credits must not overlap the stored introduction — decision
table in `Db/SegmentWriteDecision.cs`) cannot be bypassed. The facades are stateless
apart from their initialization gate: every operation creates a fresh short-lived
context from the injected factory, so the concurrency model is unchanged from the
pre-redesign `using var db = ...` discipline. `IntroSkipperDatabase` is split across
partial files by aggregate (`.Segments`, `.SeasonStates`, `.Maintenance`); the cache
facade is a single file.

Maintenance operations that mutate both segments and season state are exposed as
domain-level facade methods and commit both changes in one transaction. Detection-cache
cleanup runs afterward on a best-effort basis: cache failures are logged but cannot leave
the authoritative segment database half-updated.

## 2. Initialization: one-shot gates inside the facades

Each facade owns a one-shot gate — `Lazy<Task>` (segment DB) / `Lazy<bool>` (cache DB,
whose consumers are synchronous) with `LazyThreadSafetyMode.ExecutionAndPublication` —
and **every public member awaits the gate before touching the database**.

**Ordering argument.** `ExecutionAndPublication` guarantees the initialization core is
invoked exactly once and all racers receive the *same* `Task` instance; a `Task` cannot
complete before its body finishes; therefore no query in any facade operation can be
issued before legacy repair + `MigrateAsync` (or the cache's `EnsureSchema`) have
returned — for every possible interleaving of first callers (early playback through
`SegmentProvider`, an early controller hit, the hosted warm-up, or a scheduled task).

**Gates never cache faults.** Both initialization cores end in a catch-all that logs
and swallows. This preserves the plugin's historical constructor behavior (subsequent
operations run against whatever schema exists and surface their own errors), and it is
load-bearing for the `Lazy` gates: an escaping exception would be cached by the gate
and rethrown on every subsequent operation, permanently poisoning the database for the
process lifetime.

Tests construct sibling facade instances with independent gates over the same file;
initialization tolerates that: legacy repair is existence-check-guarded and
transactional (§7), and `MigrateAsync` takes EF's own migration lock
(`ConcurrentFacadeInstances_InitializeLegacyDatabaseWithoutErrors` pins this).

## 3. Eager warm-up: `IntroSkipperDatabaseInitializer`

A thin `IHostedService`, registered before `Entrypoint`, calls both facades'
initialization so migrations are warmed before first use; it contains no logic beyond
that. It only moves the one-time cost off the first request — correctness never depends
on it, because the gates guarantee ordering for any request that arrives earlier.
Each warm-up call is wrapped in its own catch-all: the method runs inside Jellyfin's
host startup, so an unhandled exception would abort the *entire server* — every plugin
plus Jellyfin itself — over what is, at worst, a plugin database file. The facades
swallow their own init failures by contract, but the warm-up must be safe against
facade bugs and exotic escapes on its own.

## 4. DI registration

Standard non-pooled `AddDbContextFactory<TContext>` per context, with the SQLite path
resolved from `IApplicationPaths` via `IntroSkipperDatabasePaths` (single source of
truth for both file paths) and a shared stateless `SqlitePragmaInterceptor`
(`busy_timeout=5000` on connection open). The facade singletons consume these factories
directly. **Non-pooled** because pooling requires a single public options-constructor
and skips `OnConfiguring`, which would forbid the string-path constructor that the
design-time factory, the rebuild flow and the tests rely on — and the plugin's query
rate (a handful per playback/scan) makes µs-scale context reuse unmeasurable here.

## 5. Unbounded ID sets: `EF.Parameter` / `json_each`

EF Core 10's default translation of parameterized collections on SQLite produces
discrete padded parameters, and SQLite rejects statements above
`SQLITE_MAX_VARIABLE_NUMBER` = 32,766. Every large-set operation (retention sweeps,
batch deletes, season snapshots, cache stale-scan) therefore wraps the collection in
`EF.Parameter(...)`, which binds the whole set as a **single JSON parameter** translated
through `json_each` — one statement at any size, measured ~21x faster than Chunk(500)
at 33k IDs. `json_each` availability is guaranteed because the plugin bundles
`SQLitePCLRaw.lib.e_sqlite3`; the system SQLite is never used. Each converted operation
is pinned by a 33,000-ID test. Small captured collections (≤5 elements) intentionally
keep the default translation.

## 6. WAL enforcement during initialization

Both initialization cores run an idempotent `PRAGMA journal_mode=WAL;` after
repair/migration (or `EnsureSchema`). WAL is a persistent database property, but EF
only sets it when *it* creates the database file — a database vacuumed into
rollback-journal mode or recreated by external tooling would otherwise stay non-WAL
forever. The pragma runs inside the existing catch-all, so the never-faulting gate
contract is untouched.

## 7. Legacy schema repair

`EnsureLegacySchemaCompatibility` normalizes pre-EF-migration databases (missing
migration history, missing columns) before `MigrateAsync`, so recovery does not log a
false initialization failure. It is re-runnable by construction: every step is guarded
by an existence check, and the check-then-alter sequence runs inside a transaction —
Microsoft.Data.Sqlite issues `BEGIN IMMEDIATE`, acquiring the write lock before the
checks run, so concurrent repairs are strictly serialized and the loser observes the
winner's committed schema. `RebuildDatabaseAsync` (salvage flow) remains a facade
member using the same factory.
