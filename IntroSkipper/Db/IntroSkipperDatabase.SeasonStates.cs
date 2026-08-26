// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Season-state (<see cref="DbSeasonState"/>) operations of <see cref="IntroSkipperDatabase"/>,
/// plus the queue-verification snapshot that joins season state, analysis records and segments.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <inheritdoc/>
    public async Task SetAnalyzerActionAsync(Guid seasonId, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analyzerActions);

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var existingEntries = await db.SeasonStates
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
                db.SeasonStates.Add(new DbSeasonState(seasonId, mode, action));
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>> GetSettleReanalysisStatesAsync(
        Guid seasonId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var states = await db.SeasonStates
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

        // Same JSON shape EF writes for the primitive collection, so the raw upsert and
        // tracked reads agree.
        var settledEpisodeIds = JsonSerializer.Serialize(episodeIds, (JsonSerializerOptions?)null);

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        foreach (var mode in modes)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "SeasonStates" ("SeasonId", "Type", "Action", "SettledReanalysisEpisodeIds")
                VALUES ({seasonId}, {(int)mode}, {(int)AnalyzerAction.Default}, {settledEpisodeIds})
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
        var states = await db.SeasonStates
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .Select(s => new { s.Type, s.Action })
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
        var action = await db.SeasonStates
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId && s.Type == mode)
            .Select(s => (AnalyzerAction?)s.Action)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return action ?? AnalyzerAction.Default;
    }

    /// <inheritdoc/>
    public async Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var episodeIdArray = (Guid[])[.. episodeIds.Distinct()];

        var analyzerActions = await db.SeasonStates
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .Select(s => new { s.Type, s.Action })
            .ToDictionaryAsync(s => s.Type, s => s.Action, cancellationToken)
            .ConfigureAwait(false);

        var analyzed = await db.AnalyzedItems
            .AsNoTracking()
            .Where(a => EF.Parameter(episodeIdArray).Contains(a.ItemId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Project only the consumed columns: the snapshot never reads Id, ConfigHash or
        // the timestamp columns, and skipping them avoids a per-row Guid parse plus two
        // DateTime TEXT parses on a query that runs once per season per scan.
        var segments = await db.Segments
            .AsNoTracking()
            .Where(s => EF.Parameter(episodeIdArray).Contains(s.ItemId) && s.State == SegmentState.Active)
            .Select(s => new { s.ItemId, s.Type, s.StartTicks, s.EndTicks, s.Source })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SeasonQueueSnapshot(
            analyzed.ToDictionary(a => (a.ItemId, a.Type), a => a.ConfigHash),
            analyzerActions,
            segments
                .GroupBy(s => s.ItemId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyDictionary<AnalysisMode, IReadOnlyList<Segment>>)group
                        .GroupBy(s => s.Type)
                        .ToDictionary(
                            modeGroup => modeGroup.Key,
                            modeGroup => (IReadOnlyList<Segment>)[.. modeGroup
                                .OrderBy(s => s.StartTicks)
                                .Select(s => new Segment(
                                    s.ItemId,
                                    new TimeRange(TickConversions.ToSeconds(s.StartTicks), TickConversions.ToSeconds(s.EndTicks))))])),
            segments
                .Where(s => s.Source == SegmentSource.User)
                .GroupBy(s => s.Type)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlySet<Guid>)group.Select(s => s.ItemId).ToHashSet()));
    }
}
