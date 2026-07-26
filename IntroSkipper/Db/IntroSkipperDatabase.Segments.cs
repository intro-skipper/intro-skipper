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
    /// <inheritdoc/>
    public async Task UpdateTimestampAsync(
        Segment segment,
        AnalysisMode mode,
        bool isUserProvided = false,
        string configHash = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);

        await InitializeAsync().ConfigureAwait(false);
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
                await using (transaction.ConfigureAwait(false))
                {
                    var existingSegments = await db.DbSegment
                        .Where(s => s.ItemId == segment.EpisodeId && s.Type == mode)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (!isUserProvided && existingSegments.Any(s => s.IsUserProvided))
                    {
                        return;
                    }

                    if (mode == AnalysisMode.Credits && !isUserProvided)
                    {
                        var storedIntroduction = await db.DbSegment
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                s => s.ItemId == segment.EpisodeId && s.Type == AnalysisMode.Introduction,
                                cancellationToken)
                            .ConfigureAwait(false);

                        // Touching segment boundaries do not overlap.
                        if (storedIntroduction is not null
                            && segment.Start < storedIntroduction.End
                            && storedIntroduction.Start < segment.End)
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

        return ToCanonicalTimestamps(segments);
    }

    /// <summary>
    /// Reduces stored segments to one canonical timestamp per mode: the segment with the
    /// earliest start wins. Shared by the per-episode timestamp API and the season queue
    /// snapshot so both surfaces always report the same segment.
    /// </summary>
    /// <param name="segments">Stored segments of a single episode.</param>
    /// <returns>The canonical timestamp per analysis mode.</returns>
    private static Dictionary<AnalysisMode, Segment> ToCanonicalTimestamps(IEnumerable<DbSegment> segments)
        => segments
            .GroupBy(segment => segment.Type)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(segment => segment.Start).First().ToSegment());

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        return await db.DbSegment
            .AsNoTracking()
            .Where(s => s.ItemId == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteItemSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        await db.DbSegment
            .Where(s => s.ItemId == itemId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> DeleteTimestampAsync(
        Guid itemId,
        AnalysisMode mode,
        Segment? segment = null,
        CancellationToken cancellationToken = default)
    {
        if (segment is null && mode == AnalysisMode.Commercial)
        {
            return [];
        }

        await InitializeAsync().ConfigureAwait(false);
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

        return entries;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> ReplaceItemSegmentsAsync(
        Guid itemId,
        IReadOnlyCollection<AnalysisMode> modes,
        IReadOnlyCollection<DbSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(segments);

        if (modes.Count == 0)
        {
            throw new ArgumentException("At least one analysis mode is required.", nameof(modes));
        }

        var modeArray = modes.Distinct().ToArray();
        var commercialSegments = new List<DbSegment>();
        var seenNonCommercialModes = new HashSet<AnalysisMode>();
        foreach (var segment in segments)
        {
            if (segment.ItemId != itemId)
            {
                throw new ArgumentException($"Segment item id '{segment.ItemId}' does not match '{itemId}'.", nameof(segments));
            }

            if (!modeArray.Contains(segment.Type))
            {
                throw new ArgumentException($"Segment type '{segment.Type}' is not among the replaced modes.", nameof(segments));
            }

            if (segment.Type == AnalysisMode.Commercial)
            {
                if (commercialSegments.Any(existing =>
                    RangesEquivalent(existing.Start, existing.End, segment.Start, segment.End)))
                {
                    throw new ArgumentException(
                        "Commercial segment ranges must differ by more than the comparison tolerance.",
                        nameof(segments));
                }

                commercialSegments.Add(segment);
            }
            else if (!seenNonCommercialModes.Add(segment.Type))
            {
                // IX_DbSegment_NonCommercial_Unique permits one row per (item, type), so a
                // second row of the same mode would fail the insert mid-transaction and
                // surface as a server fault. Reject it up front like the commercial
                // equivalence guard above, so both violations of the table's uniqueness
                // rules are caller errors.
                throw new ArgumentException(
                    $"Only one segment of type '{segment.Type}' is allowed per item.",
                    nameof(segments));
            }
        }

        // Replacement rows are always new database entities. Caller-owned rows may carry
        // generated ids from an earlier no-tracking query and cannot be attached alongside
        // the tracked rows being removed in this unit of work.
        var replacementRows = segments
            .Select(row => new DbSegment(row.ToSegment(), row.Type, row.IsUserProvided, row.ConfigHash))
            .ToList();

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var priorRows = await db.DbSegment
                .Where(s => s.ItemId == itemId && modeArray.Contains(s.Type))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            db.DbSegment.RemoveRange(priorRows);
            db.DbSegment.AddRange(replacementRows);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            // Detached copies: the removed entities' identity and tracking state are stale,
            // so returning them directly would make restoration a caller-side footgun.
            return priorRows
                .Select(row => new DbSegment(row.ToSegment(), row.Type, row.IsUserProvided, row.ConfigHash))
                .ToList();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteSegmentsByModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
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

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // EF.Parameter binds the ID set as a single JSON parameter (json_each), so the
        // delete is one statement regardless of the item count.
        return await db.DbSegment
            .Where(s => EF.Parameter(ids).Contains(s.ItemId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
