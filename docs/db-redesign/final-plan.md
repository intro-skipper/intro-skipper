# Final plan — IntroSkipper database layer rewrite (EF Core 10)

Outcome of the three-theory exploration (PRs #828, #822, #821) and the round-3
cross-review (`round3-cross-review.md`). Two implementation plans follow: **Plan 1
(primary): Theory B — facade per database**, and **Plan 2 (alternative): Theory A —
layered stores + domain service**. Both plans share the same verified foundation and
differ only in the shape of the seam.

## Shared foundation (identical in both plans, already proven on both branches)

- **DI**: `AddDbContextFactory<IntroSkipperDbContext>` + `AddDbContextFactory<DetectionCacheDbContext>`
  (non-pooled — pooling would forbid the string-path ctor used by the design-time
  factory, rebuild fallback, and ~30 tests, for unmeasurable gain), registered in
  `PluginServiceRegistrator`; DB paths from a single `IApplicationPaths`-derived
  helper shared with `Plugin`.
- **Lifecycle**: never-faulting one-shot init gates (legacy repair → `MigrateAsync` for
  `introskipper.db`; `EnsureSchema` create-or-recover for `introskipper-cache.db`),
  catch-all log-and-continue, independent per-database failure domains, an
  exception-proof `IHostedService` warm-up, process-wide path-keyed init serialization,
  and `PRAGMA journal_mode=WAL` enforced idempotently after init.
- **EF Core 10 usage**: `EF.Parameter(collection)` (single `json_each` JSON parameter)
  for every unbounded ID set — no manual chunking anywhere; measured ~6–21× faster than
  `Chunk(500)` and immune to `SQLITE_MAX_VARIABLE_NUMBER`; 33,000-ID pin tests on every
  such operation. Default (padded discrete-parameter) translation kept deliberately for
  small bounded sets (≤5 analysis modes). One compiled query (`EF.CompileAsyncQuery`)
  on the per-playback hot path (`GetSegmentsAsync`). `ExecuteDelete` everywhere it
  applies. No pooling, no compiled models, no query filters, no `LeftJoin` (verdicts
  recorded in the theory docs).
- **Behavioral invariants preserved and pinned by tests**: user-provided segments never
  overwritten by analysis; auto-credits/intro overlap guard; commercial multi-segment
  semantics with epsilon dedupe; rebuild/salvage flow; legacy schema repair; migration
  history untouched (existing user DBs upgrade identically).
- **End state**: zero DB code in `Plugin.cs` — no statics, no `CreateDbContext`, no
  ctor bootstrap; manually-constructed types (`BaseItemAnalyzerTask`, `QueueManager`,
  analyzers) receive the data dependency by constructor threading from DI-resolved
  roots. `Plugin.Instance` remains only for configuration/paths (separate follow-up).

---

## Plan 1 (primary) — Theory B: cohesive facade per database

**Base**: PR #822 (`capy/theory-b-db-facades`). Interfaces `IIntroSkipperDatabase`
(implementation split into partial files by aggregate: `Segments`, `SeasonStates`,
`Maintenance`) and `IDetectionCacheDatabase`. The facade is the domain boundary: all
invariant-bearing writes live inside it and consumers can never reach a `DbContext`.

**Why primary**: strongest compile-time invariant seam; the migration inventory *is*
the API (one reviewable home per database); most complete prototype (fixed three latent
>32k-parameter crash sites); most rigorously verified lifecycle (race probe + init
lock); lowest indirection for a 3-entity plugin with drive-by contributors.

### Phase 1 — Land the foundation (from the spike, ~already done)
Squash-review PR #822 as the base: facades, gates, warm-up, EF.Parameter conversions,
33k pin tests, migrated consumers (`SegmentProvider`, `SkipIntroController`,
`VisualizationController`, `CleanCacheTask`, `DetectionCacheService`), transitional
`Plugin` delegators + bridge for not-yet-threaded callers.
*Acceptance*: full suite green; every §2 inventory row in `theory-b.md` maps to a
facade method; no `Plugin.CreateDbContext` outside `Plugin` itself.

### Phase 2 — Hybrid hardening (steal from A and C; small PR)
1. Extract the non-commercial write decision (`user-precedence` + `overlap guard`)
   into an internal static pure function (Theory A's idea) called only by
   `UpdateTimestampAsync`; add direct unit tests for the decision table (no DB).
2. Enforce `PRAGMA journal_mode=WAL` at init on both DBs + assertion test
   (Theory A's round-2 change; B currently only documents it).
3. Optionally wrap the registered factories in gated decorators (Theory C's structural
   gate) so even a hypothetical future direct-factory consumer cannot precede
   migrations. Cheap; decide at review.
*Acceptance*: decision-table tests green; `journal_mode` test green.

### Phase 3 — Constructor threading (mechanical, one PR per consumer group)
1. `BaseItemAnalyzerTask(…, IIntroSkipperDatabase db)` — creators (`DetectSegmentsTask`,
   `Entrypoint`, `VisualizationController.ScanSeason`) are DI-resolved and forward it.
2. `QueueManager(…, IIntroSkipperDatabase db)`; promote `SeasonQueueSnapshot` to public
   and lift `GetSeasonQueueSnapshotAsync` onto the interface.
3. Analyzers (`ChromaprintAnalyzer`, `ChapterAnalyzer`, `BlackFrameAnalyzer`,
   `CreditsBlackFrameAnalyzer`) + `RecapDetectionHelper` (method argument) +
   `SegmentEditorController` (DI).
*Acceptance per PR*: suite green; the corresponding `Plugin` delegators become
call-less (verified by grep) and are deleted in the same PR.

### Phase 4 — Kill the transitional surface (final PR)
Delete: remaining `Plugin` delegators, `Plugin.SegmentDatabase`/`CacheDatabase` bridge,
ctor DB bootstrap, `CreateDbContext`/`CreateCacheDbContext`, `_dbPath`/`_cacheDbPath`,
`SqliteParameterBatchSize`. Move `MapSegmentTypeToMode`/`ShouldSettleReanalyze` to
`IntroSkipper.Data` helpers. Update tests off reflection-based `Plugin` scopes for DB
concerns.
*Acceptance*: `grep -c "DbContext\|DbSegment\b" Plugin.cs` → 0; suite green; a manual
upgrade test against a copied legacy fixture DB (create once from a v1.11 install)
migrates cleanly.

### Guardrails going forward
- Conventions from `theory-b.md` §1 (aggregate partials; naming; no-pass-through rule;
  split the interface along the partial seam if any file exceeds ~15 methods).
- New invariant-bearing writes must land in the facade with a decision-table test.

### Risks
- Interface growth (R2): managed by the split trigger; revisit at +5 methods.
- Wide-interface faking in consumer tests: prefer real facade over temp SQLite
  (existing repo pattern).
- Phases 3–4 are wide but type-checked and behavior-free; land as stacked PRs.

**Sizing**: Phase 1 is done (review-only); Phase 2 ~S; Phase 3 ~M (3 PRs); Phase 4 ~S.

---

## Plan 2 (alternative) — Theory A: layered stores + domain service

**Base**: PR #828 (`capy/db-redesign-theory-a`). `ISegmentStore`, `ISeasonStateStore`,
`IDetectionCacheStore` (pure persistence, internal sealed impls) + `ISegmentUpdateService`
(domain rules) + `IDatabaseInitializer`/`DatabaseStartupService` + hand-rolled factory
singletons. Cross-aggregate season operations live on `ISeasonStateStore` (season as
aggregate root); rule/write atomicity is preserved by the callback-in-transaction
primitive `ISegmentStore.ReplaceNonCommercialAsync(segment, shouldPersist)`.

**When to prefer this plan**: if the roadmap anticipates (a) more entities/aggregates,
(b) swapping or decorating persistence per aggregate (e.g. server-side segment APIs,
shared Jellyfin DB), or (c) a hard requirement that domain rules be unit-testable as
pure logic with zero DB in the loop. It buys those with ~10 types instead of ~6 and one
subtle primitive that must be policed (no I/O inside the `shouldPersist` callback).

### Phases
1. **Land the foundation** — squash-review PR #828 (stores, domain service,
   initializer, WAL enforcement, EF.Parameter conversions, pin tests, migrated slice).
   *Hardening required at review*: make `SegmentStore`'s initializer parameter
   non-nullable (test helper instead of nullable seam); document the callback contract
   on the interface with an analyzer-style test that the callback is synchronous.
2. **Hybrid hardening** — adopt B's process-wide path-keyed init serialization for the
   transitional period (two gate owners exist while `Plugin` delegators remain); adopt
   B's three latent-crash-site fixes in `VisualizationController`/`CleanCacheTask` if
   not already covered by store migration.
3. **Constructor threading** — same as Plan 1 Phase 3, but threading `ISegmentStore`/
   `ISeasonStateStore`/`ISegmentUpdateService` per consumer need (analyzers need only
   `ISegmentUpdateService` + read stores — narrower than B's single facade, at the cost
   of 2–3 parameters where B threads 1).
4. **Kill transitional surface** — same as Plan 1 Phase 4.
5. **End-state extras** — `EraseSeasonAsync`/`ClearEpisodesAsync` land on
   `ISeasonStateStore` as the two remaining multi-statement flows.

### Risks specific to this plan
- Callback primitive misuse (I/O inside a write transaction) — contract + review; if it
  recurs, replace with a closed enum-decision API.
- Interface-evolution tax: every new operation costs interface + impl (+ often service).
- Over-engineering drift: resist adding reader/writer splits; three stores + one
  service is the ceiling for this schema.

**Sizing**: Phase 1 review-only; Phases 2–5 ≈ Plan 1 Phases 2–4 + ~1 extra S PR.

---

## Decision record

- **Recommended**: Plan 1 (Theory B). Adopt Plan 2 only if the roadmap triggers listed
  above are real within a release or two.
- **Theory C** (PR #821) is eliminated for this codebase (convention-only invariant
  protection), but its gated-factory pattern is adopted as an optional hardening in
  Plan 1 Phase 2, and its test ergonomics survive in both plans (operations tested
  against real SQLite, no mocks).
- All three exploration PRs stay open as reference until the chosen plan's Phase 1
  lands, then the two losing PRs close unmerged.
