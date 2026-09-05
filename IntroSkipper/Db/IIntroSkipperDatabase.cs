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
    /// Applies one closed segment-change intent in a single transaction: the mutation,
    /// its analysis-record bookkeeping and the projection journal. Rejected intents
    /// journal nothing; ignored (already held) intents still journal a re-projection
    /// unless their target exists in no state at all. Callers must serialize calls
    /// per item (the coordinator's mutation stripe): concurrent first-time enqueues
    /// for one item can otherwise fail on the queue's primary key. Outcome semantics:
    /// <c>docs/segment-database-v2.md</c>.
    /// </summary>
    /// <param name="intent">Closed domain intent.</param>
    /// <param name="resolveExternalTarget">Resolves the Jellyfin row an
    /// <see cref="EditorDeleteSegmentIntent"/> addresses; invoked at most once, inside
    /// the transaction, only after the correlated lookup misses. Ignored for other
    /// intents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mutation outcome; <see cref="MutationResult.Outcome"/> is
    /// <see langword="null"/> when the change committed.</returns>
    Task<MutationResult> ApplyChangeAsync(SegmentChangeIntent intent, Func<Task<ExternalSegmentTarget?>>? resolveExternalTarget = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces the active automatic segments the writing pass produced for
    /// an item and mode with the admitted subset of <paramref name="segments"/>
    /// (<see cref="AutoSegmentAdmissionPolicy"/>: tombstones, user rows and intro
    /// overlap for credits reject a candidate; exact matches of the other pass or of
    /// an earlier candidate are dropped). Rows whose boundaries match an accepted
    /// segment keep their ids; an empty list clears the pass's rows; a non-empty list
    /// whose candidates were all rejected leaves the standing rows untouched. User
    /// segments and tombstones are never touched. A write that changes the servable
    /// image journals the item's projection in the same transaction.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode the segments belong to.</param>
    /// <param name="segments">Detected segments in seconds.</param>
    /// <param name="source">Analyzer that produced the segments; must not be <see cref="SegmentSource.User"/>.</param>
    /// <param name="configHash">Configuration hash that produced the segments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of the pass's active automatic segments written or kept;
    /// 0 for a fully rejected write.</returns>
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
    /// client-facing surface (the Jellyfin mirror and the provider) reads through
    /// this; editor and analysis reads use <see cref="GetSegmentsAsync"/>.
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
    /// record, in one transaction, with every item's projection journaled. Season state
    /// and disable flags are untouched. The ID set is bound as one JSON parameter, so
    /// the item count is unbounded.
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
    /// Removes the pass's stale automatic segments for the supplied items: active rows
    /// whose <see cref="DbSegment.ConfigHash"/> is non-empty and differs from
    /// <paramref name="configHash"/>. Credits-derived rows belong to the credits pass.
    /// User segments, tombstones, rows with an empty hash and the automatic rows of a
    /// type the item holds an active user row for are kept. The affected items'
    /// projections are journaled with the delete.
    /// </summary>
    /// <param name="itemIds">Item IDs to inspect.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="configHash">Current configuration hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows removed.</returns>
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
    /// Returns the analyzer action and settled-season reanalysis state for every mode
    /// of a season that has a stored row; modes without a row are absent and mean
    /// <see cref="AnalyzerAction.Default"/>.
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
    /// Clears the items' automatic segments and analysis records for the modes in one
    /// transaction so the current pass re-analyzes them from scratch. User segments,
    /// tombstones and the automatic rows of a type the item holds an active user row
    /// for are kept. Items whose rows were deleted journal their projections with the
    /// reset.
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

    // Projection journal, consumed by the segment-change coordinator: read pending
    // work, complete it, or record a failed attempt. Work is enqueued only inside the
    // facade (ApplyChangeAsync and the analyzer and maintenance writes that change
    // servable state), always atomically with the mutation.

    /// <summary>
    /// Reads every pending queue row, ordered by item id. Items without a row have no
    /// pending work; one item's work is read through <see cref="ReadProjectionWorkAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Untracked queue rows.</returns>
    Task<IReadOnlyList<DbProjectionQueueItem>> GetProjectionQueueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the ids of items whose work is due: no backoff recorded, or the backoff
    /// has elapsed.
    /// </summary>
    /// <param name="now">Current UTC time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The due item ids.</returns>
    Task<IReadOnlyList<Guid>> GetDueProjectionItemIdsAsync(DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one item's pending work: the untracked queue row plus its journaled
    /// foreign-row deletes in FIFO order.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The work, or <see langword="null"/> when nothing is pending.</returns>
    Task<(DbProjectionQueueItem Item, IReadOnlyList<DbProjectionExternalOperation> Operations)?> ReadProjectionWorkAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Completes applied work: deletes the processed operations, then the queue row,
    /// the latter only when its version still matches, so work enqueued while the
    /// apply was in flight survives. The two deletes are separate statements on
    /// purpose: a crash between them leaves the row, which costs one extra idempotent
    /// re-sync.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="version">The queue-row version the caller projected.</param>
    /// <param name="processedOperationIds">Ids of the operations the apply processed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the queue row was retired at the projected
    /// version; <see langword="false"/> when it survived because newer work superseded
    /// the version mid-apply, so the item is still behind.</returns>
    Task<bool> CompleteProjectionWorkAsync(Guid itemId, long version, IReadOnlyList<long> processedOperationIds, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed attempt on the item's queue row: increments the attempt count
    /// and stores the backoff due time and sanitized failure. Guarded by the version
    /// the failed attempt projected, like the completion: a no-op when the row is
    /// gone (the work completed concurrently) or superseded (a newer enqueue made the
    /// work due immediately, and that must not be stomped with a stale backoff).
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="version">The queue-row version the failed attempt projected.</param>
    /// <param name="nextAttemptAt">UTC time the next attempt is due.</param>
    /// <param name="failure">Sanitized failure message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RecordProjectionFailureAsync(Guid itemId, long version, DateTime nextAttemptAt, string failure, CancellationToken cancellationToken);
}
