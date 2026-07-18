// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Season-state (<see cref="DbSeasonState"/>) operations of <see cref="IntroSkipperDatabase"/>.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <inheritdoc/>
    public async Task SetAnalyzerActionAsync(Guid seasonId, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analyzerActions);

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
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
        ArgumentNullException.ThrowIfNull(episodeIds);

        // EF Core maps IEnumerable<Guid> as a primitive collection and its change tracking
        // only accepts arrays or IList<Guid>; a lazy enumerable (e.g. the Select projection
        // passed by BaseItemAnalyzerTask) makes it throw InvalidOperationException.
        var ids = episodeIds as Guid[] ?? [.. episodeIds];

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var seasonState = await db.DbSeasonState
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        if (seasonState is null)
        {
            seasonState = new DbSeasonState(seasonId, mode, AnalyzerAction.Default, ids, configHash);
            db.DbSeasonState.Add(seasonState);
        }
        else
        {
            db.Entry(seasonState).Property(s => s.EpisodeIds).CurrentValue = ids;
            db.Entry(seasonState).Property(s => s.ConfigHash).CurrentValue = configHash;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The read and write share one <see cref="IntroSkipperDbContext"/> to keep the window for
    /// concurrent overwrites as small as possible, and the write is skipped entirely when the
    /// ID is not present in the stored list.
    /// </remarks>
    public async Task RemoveEpisodeIdAsync(Guid seasonId, AnalysisMode mode, Guid episodeId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
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
    public async Task<IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>> GetSettleReanalysisStatesAsync(
        Guid seasonId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
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
    public async Task RecordSettleReanalysisAsync(
        Guid seasonId,
        IReadOnlyCollection<AnalysisMode> modes,
        IReadOnlyCollection<Guid> episodeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(episodeIds);

        if (modes.Count == 0)
        {
            return;
        }

        var settledEpisodeIds = DbSeasonState.SerializeEpisodeIds(episodeIds);

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
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
    public async Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
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
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var state = await db.DbSeasonState
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);
        return state?.Action ?? AnalyzerAction.Default;
    }

    /// <inheritdoc/>
    public async Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
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
                    group => (IReadOnlyDictionary<AnalysisMode, Segment>)ToCanonicalTimestamps(group)),
            segments
                .Where(s => s.IsUserProvided)
                .GroupBy(s => s.Type)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlySet<Guid>)group.Select(s => s.ItemId).ToHashSet()));
    }
}
