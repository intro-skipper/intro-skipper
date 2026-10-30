// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Keeps a season's file hashes current and rebinds every orphan whose file one of the
/// season's items now holds. The analysis pass runs it before verifying a season, and the
/// cleanup task before it forgets orphans.
/// </summary>
/// <param name="database">The segment database facade.</param>
/// <param name="isGone">Answers whether the server no longer knows an item or season id.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class ItemRebinder(IntroSkipperDatabase database, Func<Guid, bool> isGone, ILogger logger)
{
    /// <summary>
    /// Hashes the season's items whose file version changed or that have no hash yet,
    /// then moves to each item the rows of the one orphan holding the same hash.
    /// </summary>
    /// <remarks>
    /// A hash that two of the season's items share, or that any other item still in the
    /// library holds too, rebinds nothing. A failure is logged, not thrown, and the season
    /// analyzes as it would without the rebind. <see cref="IntroSkipperDatabase.RebindItemsAsync"/>
    /// has the rules for what moves.
    /// </remarks>
    /// <param name="season">The resolved season.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task RebindAsync(ResolvedSeason season, CancellationToken cancellationToken)
    {
        try
        {
            await RebindCoreAsync(season, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A missed rebind costs a reanalysis; a thrown one would cost the season its
            // pass and the cleanup task its forgetting, on every run until someone erases.
            LogRebindFailed(logger, ex, season.Key);
        }
    }

    private async Task RebindCoreAsync(ResolvedSeason season, CancellationToken cancellationToken)
    {
        var episodes = season.Episodes.Where(e => e.Path.Length > 0).ToList();
        if (episodes.Count == 0)
        {
            return;
        }

        var episodeIds = episodes.Select(e => e.EpisodeId).ToArray();
        var stored = await database.GetFileHashesAsync(episodeIds, cancellationToken).ConfigureAwait(false);
        var hashes = new Dictionary<Guid, string>();
        var changed = new List<DbFileHash>();
        foreach (var episode in episodes)
        {
            if (stored.TryGetValue(episode.EpisodeId, out var row) && row.FileVersion == episode.FileVersion)
            {
                hashes[episode.EpisodeId] = row.Hash;
                if (row.SeasonId != season.Key)
                {
                    changed.Add(new DbFileHash(episode.EpisodeId, season.Key, row.Hash, row.FileVersion));
                }

                continue;
            }

            // A DVD or Blu-ray folder item has no single file to hash.
            if (Directory.Exists(episode.Path))
            {
                continue;
            }

            string hash;
            try
            {
                hash = await FileHash.ComputeAsync(episode.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not a season failure: the item only misses the chance of a rebind.
                LogHashFailed(logger, episode.Name, episode.EpisodeId, ex);
                continue;
            }

            hashes[episode.EpisodeId] = hash;
            changed.Add(new DbFileHash(episode.EpisodeId, season.Key, hash, episode.FileVersion));
        }

        await database.SaveFileHashesAsync(changed, cancellationToken).ConfigureAwait(false);

        var holders = (await database.GetFileHashHoldersAsync([.. hashes.Values], episodeIds, cancellationToken).ConfigureAwait(false))
            .ToLookup(h => h.Hash);
        var seasonHolders = hashes.Values.CountBy(hash => hash).ToDictionary();
        var rebinds = new List<ItemRebind>();
        foreach (var episode in episodes)
        {
            if (!hashes.TryGetValue(episode.EpisodeId, out var hash)
                || seasonHolders[hash] != 1
                || holders[hash].ToList() is not [var orphan]
                || !isGone(orphan.ItemId))
            {
                continue;
            }

            rebinds.Add(new ItemRebind(orphan, episode.EpisodeId, episode.FileVersion));
        }

        if (rebinds.Count == 0)
        {
            return;
        }

        Guid? previousSeasonId = rebinds.Count == episodes.Count
            && rebinds.Select(r => r.Orphan.SeasonId).Distinct().ToList() is [var previous]
            && previous != season.Key
            && isGone(previous)
                ? previous
                : null;
        await database.RebindItemsAsync(rebinds, season.Key, previousSeasonId, cancellationToken).ConfigureAwait(false);
        LogRebound(logger, rebinds.Count, season.Key);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not hash the file of {Name} ({ItemId})")]
    private static partial void LogHashFailed(ILogger logger, string name, Guid itemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not move the rows of renamed or moved files in season {SeasonKey}; their new items analyze from scratch")]
    private static partial void LogRebindFailed(ILogger logger, Exception exception, Guid seasonKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved the segments and analysis of {Count} renamed or moved files to their new items in season {SeasonKey}")]
    private static partial void LogRebound(ILogger logger, int count, Guid seasonKey);
}
