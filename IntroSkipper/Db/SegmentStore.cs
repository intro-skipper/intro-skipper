// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Default <see cref="ISegmentStore"/> implementation over <see cref="IDbContextFactory{TContext}"/>.
/// Every operation awaits the <see cref="IDatabaseInitializer"/> gate (when supplied) before opening
/// a context, guaranteeing migrations have completed before any query runs.
/// </summary>
internal sealed class SegmentStore : ISegmentStore
{
    // Hot path: runs on every playback via SegmentProvider.GetMediaSegments. Compiling once skips
    // per-call LINQ expression construction and query-cache lookups.
    private static readonly Func<IntroSkipperDbContext, Guid, IAsyncEnumerable<DbSegment>> _segmentsByItemQuery =
        EF.CompileAsyncQuery((IntroSkipperDbContext db, Guid itemId) =>
            db.DbSegment.AsNoTracking().Where(s => s.ItemId == itemId));

    private readonly IDbContextFactory<IntroSkipperDbContext> _contextFactory;
    private readonly IDatabaseInitializer? _initializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentStore"/> class.
    /// </summary>
    /// <param name="contextFactory">Segment database context factory.</param>
    /// <param name="initializer">Optional initialization gate. Pass <see langword="null"/> only when the schema is guaranteed to exist already.</param>
    public SegmentStore(IDbContextFactory<IntroSkipperDbContext> contextFactory, IDatabaseInitializer? initializer = null)
    {
        _contextFactory = contextFactory;
        _initializer = initializer;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        var segments = new List<DbSegment>();
        await foreach (var segment in _segmentsByItemQuery(db, itemId).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            segments.Add(segment);
        }

        return segments;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var segments = await GetSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);

        return segments
            .GroupBy(segment => segment.Type)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(segment => segment.Start).First().ToSegment());
    }

    /// <inheritdoc/>
    public async Task<bool> TryAddCommercialAsync(DbSegment segment, double epsilon, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);

        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        var exists = await db.DbSegment
            .AnyAsync(
                s => s.ItemId == segment.ItemId
                     && s.Type == segment.Type
                     && Math.Abs(s.Start - segment.Start) <= epsilon
                     && Math.Abs(s.End - segment.End) <= epsilon,
                cancellationToken)
            .ConfigureAwait(false);

        if (exists)
        {
            return false;
        }

        db.DbSegment.Add(segment);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> ReplaceNonCommercialAsync(DbSegment segment, Func<NonCommercialSegmentContext, bool> shouldPersist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(shouldPersist);

        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingSegments = await db.DbSegment
                .Where(s => s.ItemId == segment.ItemId && s.Type == segment.Type)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            DbSegment? storedIntroduction = null;
            if (segment.Type != AnalysisMode.Introduction)
            {
                storedIntroduction = await db.DbSegment
                    .AsNoTracking()
                    .Where(s => s.ItemId == segment.ItemId && s.Type == AnalysisMode.Introduction)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!shouldPersist(new NonCommercialSegmentContext(existingSegments, storedIntroduction)))
            {
                return false;
            }

            if (existingSegments.Count > 0)
            {
                db.DbSegment.RemoveRange(existingSegments);
            }

            db.DbSegment.Add(segment);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await db.DbSegment
            .Where(s => s.ItemId == itemId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteSegmentsAsync(Guid itemId, AnalysisMode mode, Segment? match, double epsilon, CancellationToken cancellationToken = default)
    {
        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.DbSegment.Where(s => s.ItemId == itemId && s.Type == mode);

        if (match is not null)
        {
            query = query.Where(s =>
                Math.Abs(s.Start - match.Start) <= epsilon
                && Math.Abs(s.End - match.End) <= epsilon);
        }

        var entries = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (entries.Count > 0)
        {
            db.DbSegment.RemoveRange(entries);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteSegmentsByTypeAsync(AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await db.DbSegment
            .Where(s => s.Type == mode)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task CleanTimestampsAsync(IReadOnlyCollection<Guid> enabledItemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enabledItemIds);
        var enabledIds = enabledItemIds as Guid[] ?? [.. enabledItemIds];

        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);

        // EF.Parameter forces the single-JSON-parameter translation (json_each) instead of EF 10's
        // default one-parameter-per-value expansion, which would exceed SQLite's 32766-variable
        // limit for large libraries. See docs/db-redesign/theory-a.md for the measured proof.
        await db.DbSegment
            .Where(s => !EF.Parameter(enabledIds).Contains(s.ItemId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task CleanStaleAutomaticSegmentsAsync(IReadOnlyCollection<Guid> itemIds, AnalysisMode mode, string configHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await db.DbSegment
            .Where(s => EF.Parameter(ids).Contains(s.ItemId)
                && s.Type == mode
                && !s.IsUserProvided
                && s.ConfigHash != configHash)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IntroSkipperDbContext> CreateContextAsync(CancellationToken cancellationToken)
    {
        if (_initializer is not null)
        {
            await _initializer.EnsureSegmentDbReadyAsync(cancellationToken).ConfigureAwait(false);
        }

        return _contextFactory.CreateDbContext();
    }
}
