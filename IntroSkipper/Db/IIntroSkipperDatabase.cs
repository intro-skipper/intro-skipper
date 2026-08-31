// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.SegmentChanges;

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
    /// Applies one closed segment-change intent in a single transaction: the mutation
    /// (via the same cores the single-shot methods use), its analysis-record
    /// bookkeeping, and the durable projection journal — a per-item queue marker plus
    /// any journaled foreign-row delete — so a committed change can never lose its
    /// projection to a crash. Invalid intents and unowned external targets return
    /// <see cref="Rejected"/> and journal nothing. Intents that already hold return
    /// <see cref="Ignored"/> but still journal a re-projection: re-asserting held
    /// state is how a diverged mirror heals on retry. Callers must serialize calls
    /// per item (the coordinator's mutation stripe); concurrent first-time enqueues
    /// for one item can otherwise fail on the queue's primary key.
    /// </summary>
    /// <param name="intent">Closed domain intent.</param>
    /// <param name="externalTarget">The resolved Jellyfin row for
    /// <see cref="DeleteExternalSegmentIntent"/> (<see langword="null"/> when
    /// unresolved); ignored for other intents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mutation outcome; <see cref="MutationResult.Outcome"/> is
    /// <see langword="null"/> when the change committed.</returns>
    Task<MutationResult> ApplyChangeAsync(SegmentChangeIntent intent, ExternalSegmentTarget? externalTarget = null, CancellationToken cancellationToken = default);

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
    /// A non-empty list whose candidates are all rejected leaves the pass's standing rows
    /// untouched: the rejections record human intent or policy, not stale detection.
    /// User segments and tombstones are never touched.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode the segments belong to.</param>
    /// <param name="segments">Detected segments in seconds.</param>
    /// <param name="source">Analyzer that produced the segments; must not be <see cref="SegmentSource.User"/>.</param>
    /// <param name="configHash">Configuration hash that produced the segments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of active automatic segments of the writing pass written or kept by this
    /// write; 0 for a fully rejected write, whose standing rows are left as they were.</returns>
    /// <remarks>
    /// A write that changes the item's servable image journals the item's projection
    /// in the same transaction, so the projection worker converges Jellyfin even if
    /// the process dies before any mirror push.
    /// </remarks>
    Task<int> ReplaceAutoSegmentsAsync(Guid itemId, AnalysisMode mode, IReadOnlyList<Segment> segments, SegmentSource source, string configHash = "", CancellationToken cancellationToken = default);

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
    /// regenerate those rows. Everything runs in one transaction, the affected items'
    /// projections journaled with it, so their Jellyfin mirrors converge durably.
    /// </summary>
    /// <param name="mode">Analysis mode to erase.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ids of the items that held a segment of the mode.</returns>
    Task<IReadOnlyCollection<Guid>> DeleteSegmentsByModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Erases the supplied items: every segment (tombstones included) and every analysis
    /// record, in one transaction, so items still in the library are re-analyzed from
    /// scratch on the next scan and items that left it (or had their media replaced)
    /// leave nothing behind. Season state and disable flags are untouched. The ID set is
    /// bound as one JSON parameter, so the item count is unbounded. Items that held rows
    /// journal their projections with the erase, so their Jellyfin mirrors converge
    /// durably — items already gone from the library included.
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
    /// <returns>The number of rows removed.</returns>
    /// <remarks>
    /// The affected items' projections are journaled with the delete, so the removed
    /// rows reach the Jellyfin mirror even when the following analysis detects
    /// nothing new — and even if the process dies before any mirror push.
    /// </remarks>
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
    /// (the analyzers skip such an item, so nothing would regenerate them). Items whose
    /// rows were deleted journal their projections with the reset, so the removals reach
    /// the Jellyfin mirror even when the recompute finds nothing.
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
    /// and the modes their active segments cover, used by queue verification to avoid
    /// per-episode database lookups. The episode ID set is bound as one JSON parameter, so
    /// the episode count is unbounded.
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
