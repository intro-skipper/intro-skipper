// SPDX-FileCopyrightText: 2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Shared operations on <see cref="IntroSkipperDbContext.DbSegment"/>, exposed as extension
/// methods on the context so multi-step logic and domain invariants have exactly one home
/// while callers keep full control over context lifetime and transactions.
///
/// <para><b>Invariant home:</b> <see cref="UpdateTimestampAsync"/> is the ONLY code path that
/// may insert or replace <see cref="DbSegment"/> rows. It enforces two domain rules:
/// (1) analysis results never overwrite user-provided segments, and
/// (2) auto-detected credits are rejected when they overlap the stored introduction.
/// New call sites must not use <c>DbSegment.Add</c>/<c>AddRange</c> directly — reads and
/// deletes may be written inline, writes may not.</para>
/// </summary>
public static partial class SegmentOperations
{
    /// <summary>
    /// Maximum number of SQL parameters used per batch when translating ID collections.
    /// EF Core 10 translates parameterized collections to one scalar parameter per element
    /// (padded), and SQLite rejects statements above SQLITE_MAX_VARIABLE_NUMBER (32766 for
    /// SQLite ≥ 3.32). 500 keeps individual statements small and plans cacheable while
    /// staying far below the hard limit.
    /// </summary>
    internal const int SqliteParameterBatchSize = 500;

    private const double SegmentComparisonEpsilon = 0.001;

    private static readonly Func<IntroSkipperDbContext, Guid, IAsyncEnumerable<DbSegment>> _segmentsForItemQuery =
        EF.CompileAsyncQuery((IntroSkipperDbContext db, Guid itemId) =>
            db.DbSegment.AsNoTracking().Where(s => s.ItemId == itemId));

    /// <summary>
    /// Inserts or replaces the stored segment for an item and analysis mode, enforcing the
    /// domain invariants described on <see cref="SegmentOperations"/>.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="segment">Segment to store.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="isUserProvided">Whether the segment was provided by a user.</param>
    /// <param name="configHash">Analysis configuration hash.</param>
    /// <param name="logger">Optional logger for invariant diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task UpdateTimestampAsync(
        this IntroSkipperDbContext db,
        Segment segment,
        AnalysisMode mode,
        bool isUserProvided = false,
        string configHash = "",
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(segment);

        try
        {
            var dbSegment = new DbSegment(segment, mode, isUserProvided, configHash);

            if (mode == AnalysisMode.Commercial)
            {
                var exists = await db.DbSegment
                    .AnyAsync(
                        s => s.ItemId == segment.EpisodeId
                             && s.Type == mode
                             && Math.Abs(s.Start - dbSegment.Start) <= SegmentComparisonEpsilon
                             && Math.Abs(s.End - dbSegment.End) <= SegmentComparisonEpsilon,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!exists)
                {
                    db.DbSegment.Add(dbSegment);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var existingSegments = await db.DbSegment
                        .Where(s => s.ItemId == segment.EpisodeId && s.Type == mode)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    // Do not overwrite a user-provided segment with an analysis result.
                    if (!isUserProvided && existingSegments.Any(s => s.IsUserProvided))
                    {
                        return;
                    }

                    // Guard: prevent auto-detected credits from overlapping with the introduction.
                    if (mode == AnalysisMode.Credits && !isUserProvided)
                    {
                        var intro = await db.DbSegment
                            .AsNoTracking()
                            .Where(s => s.ItemId == segment.EpisodeId && s.Type == AnalysisMode.Introduction)
                            .FirstOrDefaultAsync(cancellationToken)
                            .ConfigureAwait(false);

                        if (intro is not null && segment.Start < intro.End && intro.Start < segment.End)
                        {
                            if (logger is not null)
                            {
                                LogCreditsOverlapWithIntro(logger, segment.EpisodeId);
                            }

                            return;
                        }
                    }

                    if (existingSegments.Count > 0)
                    {
                        db.DbSegment.RemoveRange(existingSegments);
                    }

                    db.DbSegment.Add(dbSegment);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            if (logger is not null)
            {
                LogFailedToUpdateTimestamp(logger, ex, segment.EpisodeId);
            }

            throw;
        }
    }

    /// <summary>
    /// Returns all stored segments for an item.
    /// Hot path: runs for every playback via <c>SegmentProvider</c>, so the query is compiled.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All stored segments for the item.</returns>
    public static async Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(
        this IntroSkipperDbContext db,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var segments = new List<DbSegment>();
        await foreach (var segment in _segmentsForItemQuery(db, itemId).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            segments.Add(segment);
        }

        return segments;
    }

    /// <summary>
    /// Returns the earliest stored segment per analysis mode for an item.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Timestamps keyed by analysis mode.</returns>
    public static async Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(
        this IntroSkipperDbContext db,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var segments = await db.GetSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        return ToTimestampDictionary(segments);
    }

    /// <summary>
    /// Deletes all segments stored for an item.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task DeleteItemSegmentsAsync(
        this IntroSkipperDbContext db,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        await db.DbSegment
            .Where(s => s.ItemId == itemId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a stored segment for the specified item and analysis mode, optionally matching
    /// exact start/end times (required to disambiguate commercial segments).
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="segment">Optional segment details used to remove a specific entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task DeleteTimestampAsync(
        this IntroSkipperDbContext db,
        Guid itemId,
        AnalysisMode mode,
        Segment? segment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (segment is null && mode == AnalysisMode.Commercial)
        {
            return;
        }

        var query = db.DbSegment.Where(s => s.ItemId == itemId && s.Type == mode);

        if (segment is not null)
        {
            query = query.Where(s =>
                Math.Abs(s.Start - segment.Start) <= SegmentComparisonEpsilon
                && Math.Abs(s.End - segment.End) <= SegmentComparisonEpsilon);
        }

        var entries = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (entries.Count > 0)
        {
            db.DbSegment.RemoveRange(entries);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes segments belonging to items that are no longer in any enabled library.
    /// Deletes are batched to stay below the SQLite parameter limit.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="enabledEpisodeIds">Episode IDs that remain enabled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task CleanTimestampsAsync(
        this IntroSkipperDbContext db,
        IEnumerable<Guid> enabledEpisodeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(enabledEpisodeIds);

        var enabledIds = enabledEpisodeIds.ToHashSet();

        var segmentEpisodeIds = await db.DbSegment
            .AsNoTracking()
            .Select(s => s.ItemId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var staleEpisodeIds = segmentEpisodeIds
            .Where(id => !enabledIds.Contains(id))
            .ToArray();

        foreach (var staleEpisodeIdBatch in staleEpisodeIds.Chunk(SqliteParameterBatchSize))
        {
            await db.DbSegment
                .Where(s => staleEpisodeIdBatch.Contains(s.ItemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes stale automatic segments for the supplied items and mode.
    /// User-provided segments are intentionally preserved.
    /// Deletes are batched to stay below the SQLite parameter limit.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="itemIds">Item IDs to inspect.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="configHash">Current configuration hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task CleanStaleAutomaticSegmentsAsync(
        this IntroSkipperDbContext db,
        IEnumerable<Guid> itemIds,
        AnalysisMode mode,
        string configHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        foreach (var batch in ids.Chunk(SqliteParameterBatchSize))
        {
            await db.DbSegment
                .Where(s => batch.Contains(s.ItemId)
                    && s.Type == mode
                    && !s.IsUserProvided
                    && s.ConfigHash != configHash)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Groups segments by analysis mode, keeping the earliest segment per mode.
    /// </summary>
    /// <param name="segments">Segments to group.</param>
    /// <returns>Timestamps keyed by analysis mode.</returns>
    internal static IReadOnlyDictionary<AnalysisMode, Segment> ToTimestampDictionary(IReadOnlyList<DbSegment> segments)
        => segments
            .GroupBy(segment => segment.Type)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(segment => segment.Start).First().ToSegment());

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping credits for episode {EpisodeId}: detected segment overlaps with introduction")]
    private static partial void LogCreditsOverlapWithIntro(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update timestamp for episode {EpisodeId}")]
    private static partial void LogFailedToUpdateTimestamp(ILogger logger, Exception ex, Guid episodeId);
}
