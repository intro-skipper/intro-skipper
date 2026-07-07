// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Segment (<see cref="DbSegment"/>) operations of <see cref="IntroSkipperDatabase"/>.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <summary>
    /// Compiled query for the playback hot path: <see cref="GetSegmentsAsync"/> runs on
    /// every media-segment request, so the LINQ-to-SQL translation is compiled once.
    /// </summary>
    private static readonly Func<IntroSkipperDbContext, Guid, IAsyncEnumerable<DbSegment>> _segmentsForItemQuery =
        EF.CompileAsyncQuery((IntroSkipperDbContext context, Guid itemId) =>
            context.DbSegment.AsNoTracking().Where(s => s.ItemId == itemId));

    /// <inheritdoc/>
    public async Task UpdateTimestampAsync(
        Segment segment,
        AnalysisMode mode,
        bool isUserProvided = false,
        string configHash = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);

        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

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
                            LogCreditsOverlapWithIntro(_logger, segment.EpisodeId);
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
            LogFailedToUpdateTimestamp(_logger, ex, segment.EpisodeId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var segments = await GetSegmentsAsync(id, cancellationToken).ConfigureAwait(false);

        return segments
            .GroupBy(segment => segment.Type)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(segment => segment.Start).First().ToSegment());
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var segments = new List<DbSegment>();
        await foreach (var segment in _segmentsForItemQuery(db, id).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            segments.Add(segment);
        }

        return segments;
    }

    /// <inheritdoc/>
    public async Task DeleteItemSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        await db.DbSegment
            .Where(s => s.ItemId == itemId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteTimestampAsync(
        Guid itemId,
        AnalysisMode mode,
        Segment? segment = null,
        CancellationToken cancellationToken = default)
    {
        if (segment is null && mode == AnalysisMode.Commercial)
        {
            return;
        }

        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

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

    /// <inheritdoc/>
    public async Task DeleteSegmentsByModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        await db.DbSegment
            .Where(s => s.Type == mode)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteSegmentsForItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // EF.Parameter binds the ID set as a single JSON parameter (json_each), so the
        // delete is one statement regardless of the item count.
        return await db.DbSegment
            .Where(s => EF.Parameter(ids).Contains(s.ItemId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
