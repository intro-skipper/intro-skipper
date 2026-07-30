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
    /// overlapping any active introduction, or duplicates a kept row exactly.
    /// Automatic rows whose boundaries match an accepted segment exactly are kept in
    /// place (stable ids); an empty list clears the mode's automatic segments.
    /// User segments and tombstones are never touched.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode the segments belong to.</param>
    /// <param name="segments">Detected segments in seconds.</param>
    /// <param name="source">Analyzer that produced the segments; must not be <see cref="SegmentSource.User"/>.</param>
    /// <param name="configHash">Configuration hash that produced the segments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of active automatic segments stored for the mode after the write.</returns>
    Task<int> ReplaceAutoSegmentsAsync(Guid itemId, AnalysisMode mode, IReadOnlyList<Segment> segments, SegmentSource source, string configHash = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a user segment. An exact-range collision is resolved in place: an active
    /// automatic row is promoted to <see cref="SegmentSource.User"/>, a suppressed row is
    /// revived as a user segment, an existing user row is returned unchanged. Overlapping
    /// (non-identical) segments of the same mode are allowed.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="startTicks">Start time in ticks.</param>
    /// <param name="endTicks">End time in ticks; must be after <paramref name="startTicks"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored row.</returns>
    Task<DbSegment> AddUserSegmentAsync(Guid itemId, AnalysisMode mode, long startTicks, long endTicks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every active segment of the mode (any source) and stores a single user
    /// segment in one transaction. Tombstones are kept. Exists only for the deprecated
    /// singular <c>POST Episode/{id}/Timestamps</c> endpoint.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="startTicks">Start time in ticks.</param>
    /// <param name="endTicks">End time in ticks; must be after <paramref name="startTicks"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored row.</returns>
    Task<DbSegment> ReplaceUserSegmentAsync(Guid itemId, AnalysisMode mode, long startTicks, long endTicks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the boundaries of a stored segment and marks it user-provided. When another
    /// segment of the same item and mode exactly occupies the new range, the two rows merge:
    /// the occupant survives as the user segment (keeping the id Jellyfin knows) and the
    /// addressed row is removed — mirroring <see cref="AddUserSegmentAsync"/>'s in-place
    /// resolution of exact-range collisions.
    /// </summary>
    /// <param name="itemId">Item ID that must own the segment; ids on other items are treated as unknown.</param>
    /// <param name="segmentId">Segment ID.</param>
    /// <param name="startTicks">New start time in ticks.</param>
    /// <param name="endTicks">New end time in ticks; must be after <paramref name="startTicks"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The surviving row (the addressed row, or the exact-range occupant it merged into),
    /// or <c>null</c> when the id is unknown on the item or suppressed.</returns>
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
    /// Clears a tombstone, making the suppressed segment active again with its original source.
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
    /// Deletes all segments stored for an item, tombstones included (used when the item
    /// itself disappears or the user resets it explicitly).
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteItemSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every stored segment of the given analysis mode, tombstones included
    /// (explicit erase is a factory reset).
    /// </summary>
    /// <param name="mode">Analysis mode to erase.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteSegmentsByModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments stored for the given items in a single statement; the ID set
    /// is bound as one JSON parameter, so the item count is unbounded.
    /// </summary>
    /// <param name="itemIds">Item IDs whose segments should be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted segment rows.</returns>
    Task<int> DeleteSegmentsForItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments for the supplied items and clears every analyzed-episode
    /// list for the season in one transaction.
    /// </summary>
    /// <param name="seasonId">Season whose analyzed-episode lists should be cleared.</param>
    /// <param name="itemIds">Item IDs whose segments should be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task ClearSeasonAnalysisAsync(Guid seasonId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every segment for the supplied items and removes those item IDs from
    /// their seasons' analyzed-episode lists in one transaction.
    /// </summary>
    /// <param name="itemIdsBySeason">Item IDs to remove, keyed by season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted segment rows.</returns>
    Task<int> RemoveItemsFromAnalysisAsync(IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> itemIdsBySeason, CancellationToken cancellationToken = default);

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
    /// Removes stale automatic segments for the supplied items and mode.
    /// User-provided segments and tombstones are intentionally preserved.
    /// </summary>
    /// <param name="itemIds">Item IDs to inspect.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="configHash">Current configuration hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task CleanStaleAutomaticSegmentsAsync(IEnumerable<Guid> itemIds, AnalysisMode mode, string configHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the analyzer actions for a season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="analyzerActions">Analyzer actions keyed by analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task SetAnalyzerActionAsync(Guid seasonId, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the analyzed episode IDs for a season and mode.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="episodeIds">Analyzed episode IDs.</param>
    /// <param name="configHash">Configuration hash used for the analysis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task SetEpisodeIdsAsync(Guid seasonId, AnalysisMode mode, IEnumerable<Guid> episodeIds, string configHash = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a single episode ID from the season's analyzed-state list for the given mode.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="episodeId">Episode ID to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RemoveEpisodeIdAsync(Guid seasonId, AnalysisMode mode, Guid episodeId, CancellationToken cancellationToken = default);

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
    /// Clears stored automatic analysis state for a season so it is re-analyzed from
    /// scratch on the current pass. The deletes and the episode-list clear run in a
    /// single transaction; user-provided segments and tombstones are preserved.
    /// </summary>
    /// <param name="seasonId">Season ID whose analyzed-state lists should be cleared.</param>
    /// <param name="episodeIds">Episode IDs whose automatic segments should be removed.</param>
    /// <param name="modes">Analysis modes to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task ResetSeasonForReanalysisAsync(Guid seasonId, IEnumerable<Guid> episodeIds, IReadOnlyCollection<AnalysisMode> modes, CancellationToken cancellationToken = default);

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
    /// Returns a consistent snapshot of the season state and stored active segments used
    /// by queue verification, avoiding per-episode database lookups. The episode ID set is
    /// bound as one JSON parameter, so the episode count is unbounded.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="episodeIds">Episode IDs in the season.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The season queue snapshot.</returns>
    Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes season-state and disabled-item rows whose seasons no longer exist.
    /// </summary>
    /// <param name="seasonIds">Season IDs that still exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task CleanSeasonStateAsync(IEnumerable<Guid> seasonIds, CancellationToken cancellationToken = default);

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
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task SetItemDisabledAsync(Guid seasonId, Guid itemId, bool disabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the database while attempting to preserve valid segments, season state,
    /// disabled items and the legacy-import marker. The rebuild never re-runs the legacy import.
    /// </summary>
    /// <param name="forceCleanOnBackupFailure">
    /// When <c>true</c>, rebuild proceeds with an empty database if the backup read fails.
    /// When <c>false</c>, the rebuild aborts to avoid data loss.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RebuildDatabaseAsync(bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default);
}
