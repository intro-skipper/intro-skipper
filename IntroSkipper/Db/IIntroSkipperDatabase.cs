// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Defines the facade for the segment database (<c>introskipper.db</c>).
/// </summary>
/// <remarks>
/// The facade owns every read and write against <see cref="IntroSkipperDbContext"/>,
/// including segments, season state, database maintenance, and database lifecycle
/// operations such as legacy schema repair, EF migrations, and salvage rebuilds. Domain
/// write rules, including user-provided precedence and credits-introduction overlap,
/// reside here so callers never handle a <see cref="IntroSkipperDbContext"/>.
/// </remarks>
public interface IIntroSkipperDatabase
{
    /// <summary>
    /// Ensures the database is initialized (legacy schema repair + EF migrations).
    /// Concurrent callers share one attempt and successful initialization is cached.
    /// A failed attempt propagates to its callers and the next operation retries before
    /// touching the database, so calling this method eagerly is an optimization, not a requirement.
    /// </summary>
    /// <returns>A <see cref="Task"/> that completes when initialization has finished.</returns>
    Task InitializeAsync();

    /// <summary>
    /// Stores a segment for an item, enforcing the domain rules:
    /// user-provided segments are never overwritten by analysis results, and
    /// auto-detected credits must not overlap the stored introduction.
    /// Commercial segments are appended (deduplicated); other modes replace the existing row.
    /// </summary>
    /// <param name="segment">Segment to store.</param>
    /// <param name="mode">Analysis mode the segment belongs to.</param>
    /// <param name="isUserProvided">Whether the segment was provided by the user.</param>
    /// <param name="configHash">Configuration hash that produced the segment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task UpdateTimestampAsync(Segment segment, AnalysisMode mode, bool isUserProvided = false, string configHash = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the earliest stored segment per analysis mode for an item.
    /// </summary>
    /// <param name="id">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Segments keyed by analysis mode.</returns>
    Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all stored segments for an item.
    /// </summary>
    /// <param name="id">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All segments stored for the item.</returns>
    Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments stored for an item.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteItemSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a stored timestamp for an item and analysis mode.
    /// </summary>
    /// <remarks>
    /// The delete returns the exact rows it matched using the facade's comparison epsilon,
    /// including <see cref="DbSegment.IsUserProvided"/> and
    /// <see cref="DbSegment.ConfigHash"/>, so callers can restore them without duplicating
    /// the matching rule.
    /// </remarks>
    /// <param name="itemId">The ID of the item whose timestamp is removed.</param>
    /// <param name="mode">One of the analysis modes that specifies the segment type.</param>
    /// <param name="segment">The optional details used to remove a specific entry.</param>
    /// <param name="cancellationToken">The token that cancels the asynchronous database operation.</param>
    /// <returns>The deleted segment rows, or an empty list when nothing matches.</returns>
    Task<IReadOnlyList<DbSegment>> DeleteTimestampAsync(Guid itemId, AnalysisMode mode, Segment? segment = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces every stored segment of the given analysis modes for an item.
    /// </summary>
    /// <remarks>
    /// The operation is atomic. Rows outside <paramref name="modes"/> remain unchanged.
    /// The returned rows are detached copies that retain user-provided and configuration
    /// metadata and can be supplied to a later call for restoration. This deliberate
    /// editor action bypasses the auto-versus-user and credits-overlap guards used by
    /// <see cref="UpdateTimestampAsync"/>.
    /// </remarks>
    /// <param name="itemId">The ID of the item whose segments are replaced.</param>
    /// <param name="modes">The analysis modes whose rows are replaced.</param>
    /// <param name="segments">The rows that will exist for <paramref name="modes"/> after the operation.</param>
    /// <param name="cancellationToken">The token that cancels the asynchronous transaction.</param>
    /// <returns>The detached prior rows for <paramref name="modes"/>, or an empty list when there were none.</returns>
    /// <exception cref="ArgumentException">A row has another item ID or a type outside <paramref name="modes"/>, or commercial rows are equivalent within the comparison tolerance.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before the transaction commits.</exception>
    Task<IReadOnlyList<DbSegment>> ReplaceItemSegmentsAsync(Guid itemId, IReadOnlyCollection<AnalysisMode> modes, IReadOnlyCollection<DbSegment> segments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every stored segment of the given analysis mode.
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
    /// Gets the IDs of items with segments that are no longer part of any enabled library.
    /// </summary>
    /// <param name="enabledEpisodeIds">Episode IDs that are still part of enabled libraries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stale episode IDs.</returns>
    Task<IReadOnlyCollection<Guid>> GetStaleTimestampEpisodeIdsAsync(
        IEnumerable<Guid> enabledEpisodeIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes stale automatic segments for the supplied items and mode.
    /// User-provided segments are intentionally preserved.
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
    /// single transaction; user-provided segments are preserved.
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
    /// Returns a consistent snapshot of the season state and stored segments used by
    /// queue verification, avoiding per-episode database lookups. The episode ID set is
    /// bound as one JSON parameter, so the episode count is unbounded.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="episodeIds">Episode IDs in the season.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The season queue snapshot.</returns>
    Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes season-state rows whose seasons no longer exist.
    /// </summary>
    /// <param name="seasonIds">Season IDs that still exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task CleanSeasonStateAsync(IEnumerable<Guid> seasonIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the database while attempting to preserve valid segments and season state.
    /// </summary>
    /// <param name="forceCleanOnBackupFailure">
    /// When <c>true</c>, rebuild proceeds with an empty database if the backup read fails.
    /// When <c>false</c>, the rebuild aborts to avoid data loss.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RebuildDatabaseAsync(bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default);
}
