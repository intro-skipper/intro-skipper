// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Cohesive facade over the segment database (<c>introskipper-v2.db</c>).
/// Owns every read and write against <see cref="IntroSkipperDbContext"/> — segments,
/// season state, disabled items and database maintenance — as well as the database lifecycle
/// (EF migrations, one-time legacy import and salvage rebuild).
/// All domain rules that guard writes (user-provided precedence, tombstone
/// suppression, credits/intro overlap) live inside this facade; callers never see a
/// <c>DbContext</c>. Boundaries are ticks internally; analysis writes accept seconds
/// because analyzers work in seconds.
/// </summary>
public interface IIntroSkipperDatabase
{
    /// <summary>
    /// Ensures the database is initialized (EF migrations + one-time legacy import).
    /// Concurrent callers share one attempt and successful initialization is cached.
    /// A failed attempt propagates to its callers and the next operation retries before
    /// touching the database, so calling this method eagerly is an optimization, not a requirement.
    /// </summary>
    /// <returns>A <see cref="Task"/> that completes when initialization has finished.</returns>
    Task InitializeAsync();

    /// <summary>
    /// Atomically replaces the active automatic segments of an item and mode with the
    /// accepted subset of <paramref name="segments"/>. A segment is rejected when it is
    /// invalid (end not after start), overlaps a tombstone of the same item and mode,
    /// overlaps an active user segment of the same mode, is an automatic credits segment
    /// overlapping any active introduction, exactly matches a row of the other pass, or
    /// exactly duplicates an earlier accepted segment of the same batch.
    /// The write only replaces the rows its own pass produced: credits-derived rows
    /// (<see cref="SegmentSource.CreditsDerived"/>) belong to the credits pass, every
    /// other automatic row to the mode's own pass — the attribution rule of
    /// <see cref="CleanStaleAutomaticSegmentsAsync"/> — so the passes sharing the
    /// Preview mode cannot delete each other's rows.
    /// Automatic rows whose boundaries match an accepted segment exactly are kept in
    /// place (stable ids); an empty list clears the pass's automatic segments of the mode.
    /// User segments and tombstones are never touched.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode the segments belong to.</param>
    /// <param name="segments">Detected segments in seconds.</param>
    /// <param name="source">Analyzer that produced the segments; must not be <see cref="SegmentSource.User"/>.</param>
    /// <param name="configHash">Configuration hash that produced the segments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of active automatic segments of the writing pass stored for the mode after the write.</returns>
    Task<int> ReplaceAutoSegmentsAsync(Guid itemId, AnalysisMode mode, IReadOnlyList<Segment> segments, SegmentSource source, string configHash = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a user segment. An exact-range collision is resolved in place: an active
    /// automatic row is promoted to <see cref="SegmentSource.User"/>, a suppressed row is
    /// revived as a user segment, an existing user row is returned unchanged. Overlapping
    /// (non-identical) segments of the same mode are allowed. A row that changes hands this
    /// way loses its <see cref="DbSegment.ConfigHash"/>, which only describes analyzer output.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="startTicks">Start time in ticks.</param>
    /// <param name="endTicks">End time in ticks; must be after <paramref name="startTicks"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored row.</returns>
    Task<DbSegment> AddUserSegmentAsync(Guid itemId, AnalysisMode mode, long startTicks, long endTicks, CancellationToken cancellationToken = default);

    /// <summary>
    /// For each given mode, deletes every active segment (any source) and stores the
    /// single user segment, all in one transaction. A row that already occupies exactly
    /// the new range survives in place as the user segment (keeping the id Jellyfin
    /// knows), like <see cref="AddUserSegmentAsync"/>; other tombstones are kept. Modes
    /// absent from <paramref name="segmentsByMode"/> are untouched. Exists only for the
    /// replace-on-write legacy shims (<c>POST Episode/{id}/Timestamps</c> and the
    /// non-commercial <c>POST MediaSegmentsApi/{itemId}</c>).
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="segmentsByMode">The user segment to store per mode, in ticks; each end must be after its start.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task ReplaceUserSegmentsAsync(Guid itemId, IReadOnlyDictionary<AnalysisMode, (long StartTicks, long EndTicks)> segmentsByMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the boundaries of a stored segment and marks it user-provided. When another
    /// segment of the same item and mode exactly occupies the new range, the two rows merge:
    /// the occupant survives as the user segment (keeping the id Jellyfin knows) and the
    /// addressed row is removed — mirroring <see cref="AddUserSegmentAsync"/>'s in-place
    /// resolution of exact-range collisions, including the cleared
    /// <see cref="DbSegment.ConfigHash"/> on the surviving row.
    /// </summary>
    /// <param name="itemId">Item ID that must own the segment; ids on other items are treated as unknown.</param>
    /// <param name="segmentId">Segment ID.</param>
    /// <param name="startTicks">New start time in ticks.</param>
    /// <param name="endTicks">New end time in ticks; must be after <paramref name="startTicks"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The surviving row (the addressed row, or the exact-range occupant it merged into),
    /// or <c>null</c> when the id is unknown on the item, suppressed, or was deleted by a
    /// concurrent write before the update could commit.</returns>
    Task<DbSegment?> UpdateSegmentAsync(Guid itemId, Guid segmentId, long startTicks, long endTicks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a segment: automatic rows are tombstoned (kept as
    /// <see cref="SegmentState.Suppressed"/> so re-analysis does not re-add an
    /// overlapping automatic segment), user rows are hard-deleted.
    /// </summary>
    /// <param name="itemId">Item ID that must own the segment; ids on other items are treated as unknown.</param>
    /// <param name="segmentId">Segment ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A pre-delete snapshot sufficient to reverse the delete exactly via
    /// <see cref="UndoDeleteAsync"/>, or <c>null</c> when the id is unknown on the item or already suppressed.</returns>
    Task<DbSegment?> DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses a prior <see cref="DeleteSegmentAsync"/> exactly: flips the tombstone back
    /// to its previous state, or re-inserts the hard-deleted row verbatim (same id, source,
    /// config hash and creation time). No-op when nothing was deleted.
    /// </summary>
    /// <param name="deletedSnapshot">Snapshot returned by the delete to reverse; <c>null</c> when nothing was deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task UndoDeleteAsync(DbSegment? deletedSnapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears a tombstone, making the suppressed segment active again with its original
    /// source. The row's <see cref="DbSegment.ConfigHash"/> is dropped: the restore is
    /// recorded human intent, so the hash-driven stale cleanup must not judge the row.
    /// </summary>
    /// <param name="itemId">Item ID that must own the segment; ids on other items are treated as unknown.</param>
    /// <param name="segmentId">Segment ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The restored row, or <c>null</c> when the id is unknown on the item or not suppressed.</returns>
    Task<DbSegment?> RestoreSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a stored segment by id, regardless of state.
    /// </summary>
    /// <param name="segmentId">Segment ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or <c>null</c> when unknown.</returns>
    Task<DbSegment?> GetSegmentAsync(Guid segmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stored segments of an item, ordered by mode and start time.
    /// Tombstones are excluded unless <paramref name="includeSuppressed"/> is set.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="includeSuppressed">Whether to include suppressed rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored segments.</returns>
    Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid itemId, bool includeSuppressed = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the item's active segments as served to clients: automatic rows are
    /// withheld while the item is disabled, user-provided rows always pass. Every
    /// client-facing surface (the Jellyfin mirror, the provider, the legacy skip
    /// shims) reads through this; editor and analysis reads use
    /// <see cref="GetSegmentsAsync"/>.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The servable segments, ordered by mode and start time.</returns>
    Task<IReadOnlyList<DbSegment>> GetServableSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every stored segment of the given analysis mode, tombstones included
    /// (explicit erase is a factory reset), and the mode's analysis records so the next
    /// scan re-detects instead of classifying the erased items as <c>NoSegments</c>.
    /// Items whose erased rows were credits-derived also lose their
    /// <see cref="AnalysisMode.Credits"/> records, because only the credits pass can
    /// regenerate those rows. Everything runs in one transaction.
    /// </summary>
    /// <param name="mode">Analysis mode to erase.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ids of the items that held a segment of the mode, so the caller can
    /// converge their Jellyfin mirrors.</returns>
    Task<IReadOnlyCollection<Guid>> DeleteSegmentsByModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Erases the supplied items: every segment (tombstones included) and every analysis
    /// record, in one transaction, so items still in the library are re-analyzed from
    /// scratch on the next scan and items that left it (or had their media replaced)
    /// leave nothing behind. Season state and disable flags are untouched. The ID set is
    /// bound as one JSON parameter, so the item count is unbounded.
    /// </summary>
    /// <param name="itemIds">Item IDs to erase.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted segment rows.</returns>
    Task<int> EraseItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the IDs of items with segments (including tombstones) that are no longer
    /// part of any enabled library.
    /// </summary>
    /// <param name="enabledEpisodeIds">Episode IDs that are still part of enabled libraries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stale episode IDs.</returns>
    Task<IReadOnlyCollection<Guid>> GetStaleTimestampEpisodeIdsAsync(
        IEnumerable<Guid> enabledEpisodeIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes stale automatic segments for the supplied items and mode: active rows of
    /// the mode whose <see cref="DbSegment.ConfigHash"/> differs from <paramref name="configHash"/>.
    /// Rows are judged by the pass that produces them: credits-derived anime previews
    /// (<see cref="SegmentSource.CreditsDerived"/>, stamped with the credits hash) are
    /// cleaned by the <see cref="AnalysisMode.Credits"/> pass and ignored by the
    /// <see cref="AnalysisMode.Preview"/> pass. User-provided segments and tombstones
    /// are intentionally preserved, and so are rows with an empty hash (restored
    /// tombstones, legacy imports without a recorded hash — they make no staleness
    /// claim) and the automatic rows of any type the item holds an active user row for
    /// (the analyzers skip such items, so nothing would regenerate the rows).
    /// </summary>
    /// <param name="itemIds">Item IDs to inspect.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="configHash">Current configuration hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows removed, so the caller can converge the Jellyfin
    /// mirror even when the following analysis detects nothing new.</returns>
    Task<int> CleanStaleAutomaticSegmentsAsync(IEnumerable<Guid> itemIds, AnalysisMode mode, string configHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the analyzer actions for a season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="analyzerActions">Analyzer actions keyed by analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task SetAnalyzerActionAsync(Guid seasonId, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the items as analyzed for the mode under the given configuration hash,
    /// whether or not segments were found, replacing any earlier record of the same item
    /// and mode. Queue verification treats a matching record as settled (<c>Analyzed</c>
    /// with segments, <c>NoSegments</c> without) and a missing or mismatching one as
    /// <c>NotAnalyzed</c>.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="itemIds">Item IDs that were analyzed.</param>
    /// <param name="configHash">Configuration hash used for the analysis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task MarkItemsAnalyzedAsync(AnalysisMode mode, IEnumerable<Guid> itemIds, string configHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an item's analysis record for the given mode so the next scan analyzes it
    /// again. A no-op when no record exists.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task ClearItemAnalysisAsync(Guid itemId, AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the settled-season reanalysis state for all modes in a season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Settled reanalysis state keyed by analysis mode.</returns>
    Task<IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>> GetSettleReanalysisStatesAsync(Guid seasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that season analysis modes have been re-analyzed for the given episode set.
    /// Call only after the reset has committed.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="modes">Analysis modes that were re-analyzed.</param>
    /// <param name="episodeIds">Episode IDs that were re-analyzed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RecordSettleReanalysisAsync(Guid seasonId, IReadOnlyCollection<AnalysisMode> modes, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears stored automatic analysis state for the items so they are re-analyzed from
    /// scratch on the current pass: the modes' automatic segments and analysis records go
    /// in a single transaction; user-provided segments and tombstones are preserved, and
    /// so are the automatic rows of an item that holds an active user row of the same mode
    /// (the analyzers skip such an item, so nothing would regenerate them).
    /// </summary>
    /// <param name="itemIds">Item IDs to reset.</param>
    /// <param name="modes">Analysis modes to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task ResetItemsForReanalysisAsync(IEnumerable<Guid> itemIds, IReadOnlyCollection<AnalysisMode> modes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the analyzer action for every analysis mode of a season, filling in
    /// <see cref="AnalyzerAction.Default"/> for modes without a stored row.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analyzer actions keyed by analysis mode.</returns>
    Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(Guid seasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the analyzer action for a season and mode.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored analyzer action, or <see cref="AnalyzerAction.Default"/>.</returns>
    Task<AnalyzerAction> GetAnalyzerActionAsync(Guid seasonId, AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a snapshot of the season's analyzer actions, the episodes' analysis records
    /// and their stored active segments, used by queue verification to avoid per-episode
    /// database lookups. The episode ID set is bound as one JSON parameter, so the episode
    /// count is unbounded.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="episodeIds">Episode IDs in the season.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The season queue snapshot.</returns>
    Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the season-state keys that are not part of <paramref name="retainedSeasonIds"/>,
    /// so cleanup can decide per key whether the season is gone or merely missing from an
    /// enumeration that skipped its library.
    /// </summary>
    /// <param name="retainedSeasonIds">Season IDs known to still exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stale season-state keys.</returns>
    Task<IReadOnlyCollection<Guid>> GetStaleSeasonIdsAsync(IEnumerable<Guid> retainedSeasonIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes season-state rows whose seasons no longer exist.
    /// </summary>
    /// <param name="seasonIds">Season IDs that still exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task CleanSeasonStateAsync(IEnumerable<Guid> seasonIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the item IDs holding per-item state (disable flags or analysis records) that
    /// are not part of <paramref name="retainedItemIds"/>, so cleanup can decide per item
    /// whether it is gone or merely missing from an enumeration that skipped its library.
    /// </summary>
    /// <param name="retainedItemIds">Item IDs known to still exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stale per-item-state item IDs.</returns>
    Task<IReadOnlyCollection<Guid>> GetStaleItemStateIdsAsync(IReadOnlyCollection<Guid> retainedItemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes per-item state (disable flags and analysis records) of items that no
    /// longer exist in enabled libraries. Rows are pruned by item ID — never by a stored
    /// season key, which is mutable metadata that can go stale when an item moves season
    /// keys — so the state survives key drift and disappears only when the item does.
    /// </summary>
    /// <param name="retainedItemIds">Item IDs that still exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task CleanItemStateAsync(IReadOnlyCollection<Guid> retainedItemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the IDs of the season's items whose automatic segments are withheld
    /// from Jellyfin.
    /// </summary>
    /// <param name="seasonId">Season-state key (a movie's own ID for movies).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The disabled item IDs.</returns>
    Task<IReadOnlySet<Guid>> GetDisabledItemIdsAsync(Guid seasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets whether an item's automatic segments are withheld from Jellyfin.
    /// Analysis and stored segments are unaffected either way; user-provided segments
    /// always sync. Disabling records the item's current season key — rewriting a
    /// stale key in place — and enabling removes the flag no matter which key
    /// recorded it. Idempotent: a request matching the stored state and key writes
    /// nothing.
    /// </summary>
    /// <param name="seasonId">Season-state key that owns the item (a movie's own ID for movies).</param>
    /// <param name="itemId">Item ID.</param>
    /// <param name="disabled">Whether to withhold the item's automatic segments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the item was disabled before this write, so callers can roll back.</returns>
    Task<bool> SetItemDisabledAsync(Guid seasonId, Guid itemId, bool disabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the database while attempting to preserve segments, season state,
    /// analysis records, disabled items and the legacy-import marker. Runs even when
    /// initialization fails (it recreates the schema itself), so a database whose
    /// migrations no longer apply can still be recovered.
    /// </summary>
    /// <param name="forceCleanOnBackupFailure">
    /// When <c>true</c>, rebuild proceeds with an empty database if the backup read fails.
    /// When <c>false</c>, the rebuild aborts to avoid data loss.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    /// <exception cref="DatabaseRebuildBackupException">The backup read failed and <paramref name="forceCleanOnBackupFailure"/> is <c>false</c>; the database file is untouched.</exception>
    Task RebuildDatabaseAsync(bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default);
}
