# Segment database v2 — plural segments, tombstones, shared Jellyfin ids

## Context

The original timestamp schema was ported from XML storage: `DbSegment` began with primary key `(ItemId, Type)` — literally one segment per type per episode — and commercials were later patched in via an identity-PK rebuild plus two mutually exclusive filtered unique indexes (`Type = 4` hardcoded in migration SQL). Every layer above branched on `mode == Commercial`. Schema evolution was duplicated between six EF migrations and a hand-rolled raw-SQL repair (`EnsureLegacySchemaCompatibility`) that back-filled `__EFMigrationsHistory`. Jellyfin 12 natively supports any number of media segments per type per item; the one-per-type limit was purely plugin-side (issues #796, #863, #804).

## Decision

Redesign the schema from scratch for the 12.0 major version:

- **New file** `introskipper-v2.db` with a **single EF Core 10 baseline migration**. The legacy `introskipper.db` is read exactly once by `LegacyDatabaseImporter` (read-only connection, shape detection via `pragma_table_info` so repair-era mongrel schemas import too) and is **never modified** — it remains a rollback/downgrade path for pre-v2 plugin versions. The import commits atomically with a `DbImportRecord` marker; the marker, not the file, answers the "was import done" question. Re-importing requires deleting `introskipper-v2.db` and restarting.
- **Uniform plural model**: `Segments` rows are `(Guid Id, ItemId, Type, StartTicks, EndTicks, Source, State, ConfigHash, CreatedAt, UpdatedAt)` with ONE unique index `(ItemId, Type, StartTicks, EndTicks)` for every mode. No commercial special-casing anywhere.
- **Ticks (long)**, matching Jellyfin's `MediaSegment`. Seconds exist only at the analyzer and HTTP edges (`TickConversions`); the old 0.001-second epsilon matching is gone — equality is exact.
- **Shared ids**: `Id` is a client-generated Guid v7 pushed as Jellyfin's `MediaSegment.Id` on sync, so both databases address the same segment by the same Guid (verified: Jellyfin's `MediaSegmentManager` preserves provider-supplied ids; `[DatabaseGenerated(Identity)]` Guids only auto-generate when unset). Unchanged-boundary automatic rows keep their ids across re-analysis.
- **Provenance** (`SegmentSource`: Unknown/Chapter/Chromaprint/BlackFrame/CreditsDerived/User) replaces the `IsUserProvided` bool. `Unknown` can only originate from legacy import.
- **Tombstones** (`SegmentState.Suppressed`): deleting an automatic segment keeps the row, hidden from all normal reads and never synced, and blocks re-insertion of any strictly overlapping automatic segment of the same item+mode (issue #863). User segments hard-delete. Tombstones survive config-hash cleanup and season reanalysis; explicit erase operations (mode/season/item erase) purge them. `Restore` reactivates one.
- **Plural API**: `Episode/{itemId}/Segments` (GET/POST/PUT/DELETE/Restore, seconds at the edge). The singular `Episode/{id}/Timestamps` and `IntroSkipperSegments` endpoints stay as byte-compatible shims (collapse rule: earliest-start active segment per mode, `LegacyTimestampMapper`); `MediaSegmentsApi` keeps its external wire contract.

## Consequences

- Multiple intros/outros/commercials per episode are first-class end-to-end (storage → sync → API → config-page editor); `ChapterAnalyzer` emits every matching commercial chapter.
- Analysis writes go through one primitive, `ReplaceAutoSegmentsAsync`, whose admission rules (`AutoSegmentAdmissionPolicy`) gate automatic output only: automation may not contradict recorded human intent (tombstones, active user rows of the same mode), and automatic credits are dropped when they overlap an active introduction. These are admission rules at the write, not state invariants — overlaps are legal stored state (user writes are unconditional; restore/undo do not re-validate), and the stored state's only invariants are the exact-range unique index and end > start.
- The legacy repair machinery, migration back-fill and epsilon matching are deleted; future schema changes are plain EF migrations on the v2 file.
- Restoring an old `introskipper.db` after v2 exists does not re-import (marker); document "delete `introskipper-v2.db` + restart" in release notes as the supported re-import path.
- The plugin's own DB remains the source of truth; Jellyfin's MediaSegments table is a mirror per item (`SyncItemAsync` = replace own rows), with other providers' rows untouched.
