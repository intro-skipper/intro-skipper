// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Default <see cref="ISeasonStateStore"/> implementation over <see cref="IDbContextFactory{TContext}"/>.
/// Every operation awaits the <see cref="IDatabaseInitializer"/> gate (when supplied) before opening
/// a context, guaranteeing migrations have completed before any query runs.
/// </summary>
internal sealed class SeasonStateStore : ISeasonStateStore
{
    private readonly IDbContextFactory<IntroSkipperDbContext> _contextFactory;
    private readonly IDatabaseInitializer? _initializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonStateStore"/> class.
    /// </summary>
    /// <param name="contextFactory">Segment database context factory.</param>
    /// <param name="initializer">Optional initialization gate. Pass <see langword="null"/> only when the schema is guaranteed to exist already.</param>
    public SeasonStateStore(IDbContextFactory<IntroSkipperDbContext> contextFactory, IDatabaseInitializer? initializer = null)
    {
        _contextFactory = contextFactory;
        _initializer = initializer;
    }

    /// <inheritdoc/>
    public async Task SetAnalyzerActionsAsync(Guid seasonId, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analyzerActions);

        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        var existingEntries = await db.DbSeasonState
            .Where(s => s.SeasonId == seasonId)
            .ToDictionaryAsync(s => s.Type, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (mode, action) in analyzerActions)
        {
            if (existingEntries.TryGetValue(mode, out var existing))
            {
                db.Entry(existing).Property(s => s.Action).CurrentValue = action;
            }
            else
            {
                db.DbSeasonState.Add(new DbSeasonState(seasonId, mode, action));
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SetEpisodeIdsAsync(Guid seasonId, AnalysisMode mode, IEnumerable<Guid> episodeIds, string configHash = "", CancellationToken cancellationToken = default)
    {
        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        var seasonState = await db.DbSeasonState
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        if (seasonState is null)
        {
            seasonState = new DbSeasonState(seasonId, mode, AnalyzerAction.Default, episodeIds, configHash);
            db.DbSeasonState.Add(seasonState);
        }
        else
        {
            db.Entry(seasonState).Property(s => s.EpisodeIds).CurrentValue = episodeIds;
            db.Entry(seasonState).Property(s => s.ConfigHash).CurrentValue = configHash;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RemoveEpisodeIdAsync(Guid seasonId, AnalysisMode mode, Guid episodeId, CancellationToken cancellationToken = default)
    {
        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        var seasonState = await db.DbSeasonState
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        if (seasonState is null)
        {
            return;
        }

        var currentIds = seasonState.EpisodeIds.ToList();
        if (!currentIds.Remove(episodeId))
        {
            return; // Episode was not in the list — no write needed.
        }

        db.Entry(seasonState).Property(s => s.EpisodeIds).CurrentValue = currentIds;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<AnalysisMode, IEnumerable<Guid>>> GetEpisodeIdsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.DbSeasonState.Where(s => s.SeasonId == seasonId)
            .ToDictionaryAsync(s => s.Type, s => s.EpisodeIds, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>> GetSettleReanalysisStatesAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        var states = await db.DbSeasonState
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .Select(s => new
            {
                s.Type,
                s.Action,
                s.SettledReanalysisEpisodeIds
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return states.ToDictionary(
            s => s.Type,
            s => (s.Action, (IReadOnlySet<Guid>)s.SettledReanalysisEpisodeIds.ToHashSet()));
    }

    /// <inheritdoc/>
    public async Task RecordSettleReanalysisAsync(Guid seasonId, IReadOnlyCollection<AnalysisMode> modes, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(episodeIds);
        if (modes.Count == 0)
        {
            return;
        }

        var settledEpisodeIds = DbSeasonState.SerializeEpisodeIds(episodeIds);

        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var mode in modes)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "DbSeasonState" ("SeasonId", "Type", "Action", "EpisodeIds", "ConfigHash", "SettledReanalysisEpisodeIds")
                VALUES ({seasonId}, {(int)mode}, {(int)AnalyzerAction.Default}, {"[]"}, {string.Empty}, {settledEpisodeIds})
                ON CONFLICT("SeasonId", "Type") DO UPDATE SET
                    "SettledReanalysisEpisodeIds" = excluded."SettledReanalysisEpisodeIds"
                """,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task ResetSeasonForReanalysisAsync(Guid seasonId, IEnumerable<Guid> episodeIds, IReadOnlyCollection<AnalysisMode> modes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episodeIds);
        ArgumentNullException.ThrowIfNull(modes);

        var ids = episodeIds.Distinct().ToArray();
        var modeArray = modes.ToArray();
        if (ids.Length == 0 || modeArray.Length == 0)
        {
            return;
        }

        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await db.DbSegment
                .Where(s => EF.Parameter(ids).Contains(s.ItemId) && modeArray.Contains(s.Type) && !s.IsUserProvided)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            // Clear the analyzed-episode lists so VerifyQueueAsync treats every episode as NotAnalyzed.
            // Committing this together with the deletes guarantees the episodes are re-analyzed (either
            // on this pass or a later one) instead of being stranded as NoSegments.
            var seasonStates = await db.DbSeasonState
                .Where(s => s.SeasonId == seasonId && modeArray.Contains(s.Type))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var state in seasonStates)
            {
                db.Entry(state).Property(s => s.EpisodeIds).CurrentValue = [];
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episodeIds);

        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        var episodeIdArray = (Guid[])[.. episodeIds.Distinct()];

        var seasonStates = await db.DbSeasonState
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var segments = await db.DbSegment
            .AsNoTracking()
            .Where(s => EF.Parameter(episodeIdArray).Contains(s.ItemId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SeasonQueueSnapshot(
            seasonStates.ToDictionary(s => s.Type, s => (IReadOnlySet<Guid>)s.EpisodeIds.ToHashSet()),
            seasonStates.ToDictionary(s => s.Type, s => s.ConfigHash),
            seasonStates.ToDictionary(s => s.Type, s => s.Action),
            segments
                .GroupBy(s => s.ItemId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyDictionary<AnalysisMode, Segment>)group
                        .GroupBy(segment => segment.Type)
                        .ToDictionary(
                            segmentGroup => segmentGroup.Key,
                            segmentGroup => segmentGroup.OrderBy(segment => segment.Start).First().ToSegment())),
            segments
                .Where(s => s.IsUserProvided)
                .GroupBy(s => s.Type)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlySet<Guid>)group.Select(s => s.ItemId).ToHashSet()));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        var states = await db.DbSeasonState
            .Where(s => s.SeasonId == seasonId)
            .ToDictionaryAsync(s => s.Type, s => s.Action, cancellationToken)
            .ConfigureAwait(false);

        // Fill in defaults for any missing modes
        var result = new Dictionary<AnalysisMode, AnalyzerAction>();
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            result[mode] = states.TryGetValue(mode, out var action) ? action : AnalyzerAction.Default;
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<AnalyzerAction> GetAnalyzerActionAsync(Guid seasonId, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        var state = await db.DbSeasonState
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);
        return state?.Action ?? AnalyzerAction.Default;
    }

    /// <inheritdoc/>
    public async Task CleanSeasonStatesAsync(IReadOnlyCollection<Guid> validSeasonIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validSeasonIds);
        var ids = validSeasonIds as Guid[] ?? [.. validSeasonIds];

        using var db = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await db.DbSeasonState
            .Where(s => !EF.Parameter(ids).Contains(s.SeasonId))
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
