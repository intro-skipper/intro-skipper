# Round 3 — Cross-comparison of the three DB-layer theories

Coordinator-executed red-team review of the three revised exploration branches.
Judged state: Theory A `capy/db-redesign-theory-a` @ `822637e` (PR #828), Theory B
`capy/theory-b-db-facades` @ `c38ef5c` (PR #822), Theory C `capy/theory-c-db-layer`
@ `69f07a7` (PR #821).

## 1. Verification log

Each branch was checked out into its own git worktree and verified independently:

| Check | Theory A | Theory B | Theory C |
|---|---|---|---|
| `dotnet build IntroSkipper.sln` | 0 warnings / 0 errors | 0 warnings / 0 errors | 0 warnings / 0 errors |
| `dotnet test` | 377/378 | 382/383 | 379/380 |
| Sole failure | `TestSilenceDetection` (known environmental) | same | same |
| Invariant home traced | `SegmentUpdateService.ShouldPersist` executed inside `SegmentStore.ReplaceNonCommercialAsync` transaction — semantics verified equal to `Plugin.UpdateTimestampAsync` line-by-line | `IntroSkipperDatabase.Segments.UpdateTimestampAsync` — verbatim move, diff-verified | `SegmentOperations.UpdateTimestampAsync` extension — verbatim move, diff-verified |
| Init gate traced | `Lazy<Task>`/`Lazy<bool>` in `DatabaseInitializer`; every store op awaits before context creation | `Lazy<Task>`+`Lazy<bool>` inside each facade; every facade op awaits first; path-keyed `SemaphoreSlim` serializes DI + bridge initializers | independent per-DB never-faulting gates below `IDbContextFactory<T>.CreateDbContext[Async]` — structurally under every access path |
| Fault containment (cache init throws unexpected type) | catch-all; gate never faults; regression test `DatabaseInitializer_GatesNeverThrow_WhenInitializationFails` | catch-all in facade + independently exception-proof hosted warm-up | split gates + catch-all; fault-injection test `CacheInitializationFailure_DoesNotBlockSegmentDatabaseAccess` |
| `EF.Parameter` (json_each) adoption + 33k pin tests | yes (originated the finding) | yes (all large-set ops, incl. 3 latent unchunked sites it found in round 1) | yes (round 2; `CleanTimestampsAsync` improved to single NOT-IN `ExecuteDelete`) |
| WAL | enforced at init (`PRAGMA journal_mode=WAL`) + test | doc note (EF sets WAL at creation — verified by coordinator benchmark) | doc note + test asserting `wal` |

Coordinator arbitration benchmark (EF 10.0.7, Microsoft.Data.Sqlite, e_sqlite3, 40k-row
table, 33k-ID set): default `Contains` throws `too many SQL variables`;
`EF.Parameter` `Contains` ×5 = 235 ms vs `Chunk(500)` ×5 = 1,552 ms; `ExecuteDelete`
NOT-IN = 218 ms. Independently reproduced by Theories A and B within noise.

## 2. Findings per theory (post-round-2 residuals)

**Theory A**
- Minor behavioral delta (accepted): `ReplaceNonCommercialAsync` loads the stored intro
  for *every* non-intro write (original queried it only for Credits mode) — one extra
  indexed read per Recap/Preview/Credits write, no behavior change.
- `SegmentStore` accepts a nullable `IDatabaseInitializer` "for tests" — a footgun that
  allows constructing an ungated store; DI wiring passes it correctly, but the seam is
  convention-guarded. Should be non-nullable with a test-only factory helper.
- The `shouldPersist` callback primitive is the design's cleverest and riskiest part:
  it preserves rule/write atomicity across the layer boundary, but a contributor can put
  I/O inside a write transaction without any compiler pushback (risk R7 in its own doc).

**Theory B**
- Strongest verification culture of the three: disproved the coordinator's hypothesized
  concurrent legacy-repair race with an instrumented probe (Microsoft.Data.Sqlite issues
  `BEGIN IMMEDIATE`, so check-then-ALTER is serialized), then added a process-wide
  path-keyed init lock anyway as defense-in-depth.
- Residual: 23-method interface will keep growing; the "split at >15 methods per
  partial" convention is a promise, not a mechanism.
- Transitional bridge (`Plugin.SegmentDatabase` over a lazy path factory) is the ugliest
  transitional artifact of the three, though verified safe (stateless, same file/pragmas).
- Fixed three latent pre-existing unchunked >32k-parameter crash sites the other two
  theories did not touch in prototype (`VisualizationController.ClearExcludedTimestamps`
  segment+cache deletes, `CleanCacheTask` stale scan).

**Theory C**
- The structural weakness stands even after two rounds: nothing but convention and
  review prevents `db.DbSegment.Add(...)` at a future call site from bypassing the
  user-precedence/overlap invariants. Its own doc ranks this as "the strongest argument
  for a facade." For a GPL plugin with drive-by contributors this is the deciding flaw.
- Best gate placement of the three: gating *inside the factory* protects every current
  and future consumer by construction, including ones added carelessly.
- Best test ergonomics: operations callable on any context; zero reflection residue in
  new tests.
- Prototype migrated fewer direct call sites than B (VisualizationController and
  CleanCacheTask cache sites remain on delegators/bridge until end state).

## 3. Head-to-head scoring (1–5)

| Axis | A (stores+domain) | B (facade/DB) | C (no-repository) |
|---|---|---|---|
| 1. Correctness & invariant safety | **5** (rules isolated *and* atomic; strong seam) | **5** (verbatim fidelity; strongest seam — consumers can never reach a context) | **3** (verbatim fidelity; convention-only seam) |
| 2. Init/lifecycle robustness | 4 (gate inside stores; factory itself ungated) | 4.5 (gate inside facades + init lock + race probe; facades are sole context consumers) | **5** (per-DB never-faulting gates below the factory — structurally universal) |
| 3. Migration completeness & transition cost | 4 | **5** (most sites migrated; fixed 3 latent crash sites; mechanical end state) | 4 |
| 4. Maintainability for this project | 3 (~10 types for 3 entities; own doc concedes over-engineering line; callback primitive is a trap) | **5** ("the inventory is the API"; one home per DB; conventions documented) | 4 (least code, best discoverability, but guardrails weakest) |
| 5. EF Core 10 usage quality | **5** (found `EF.Parameter`; WAL enforcement) | **5** (most nuanced final shapes; kept default translation for ≤5-element sets deliberately) | 4.5 |
| 6. Testability | **5** (rules testable as pure logic + store tests) | 4.5 (real-SQLite facade tests; one wide interface to fake) | 4.5 (simplest tests; no seam for failure injection) |
| **Total** | **26** | **29** | **25** |

## 4. Ranking and recommendation

1. **Theory B — facade per database (primary).** Best fit for this codebase: 3 entities,
   ~20 call sites, write-path invariants, drive-by contributors. The facade makes the
   migration inventory the API, gives the invariants a compile-time seam (consumers
   physically cannot reach a `DbContext`), and its prototype is the most complete and
   most rigorously verified.
2. **Theory A — layered stores + domain service (runner-up).** Choose it if the project
   wants the domain rules unit-testable as pure logic and expects the persistence layer
   to grow (more entities, alternative storage). Its own honest assessment — "a facade
   buys ~80 % of the benefit for ~35 % of the surface" — is why it is second.
3. **Theory C — eliminated.** The EF-idiomatic case is real and its lifecycle mechanics
   are the best of the three, but convention-only invariant protection is the wrong
   trade for this project's contribution model.

**Hybridizations the winner should steal**
- From C: move the init gate *into* (or additionally under) the context factories so the
  ordering guarantee is structural rather than per-facade-method discipline.
- From A: extract the `ShouldPersist` decision logic into an internal static pure
  function unit-tested without a database, keeping the facade method as its only caller.
- From A: enforce `PRAGMA journal_mode=WAL` at init (B currently only documents it).

## 5. What all three missed

- **Non-DB mutable state on `Plugin`** (`QueuedMediaItems`, `TotalQueued`, `AnalyzeAgain`,
  `FingerprintCachePath`) keeps `Plugin.Instance` load-bearing after the DB extraction;
  a follow-up extraction (out of scope here) is needed before `Plugin` is truly thin.
- **Cross-process concurrency** (two Jellyfin instances on one DB file) — explicitly out
  of scope everywhere; fine, but no doc states what actually happens (answer: WAL +
  busy_timeout make it survivable but unsupported).
- **Upgrade-path fixture tests**: all rely on the existing legacy-schema tests; none
  added a fixture database generated by an actual old plugin release.
- **Plugin uninstall/reinstall**: DB files are left behind by design; no doc records it.
- **GUID text-casing coupling** of `json_each` translation (EF binds uppercase both
  sides — safe, but only Theory B's test suite tripped over and documented it).
