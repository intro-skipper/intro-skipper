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
    /// Excluded items are never hashed and never rebind, as the analysis pass never sees
    /// them. A hash that two of the season's items share, or that any other item still in
    /// the library holds too, rebinds nothing. A file that cannot be read only misses its
    /// rebind; a database failure throws. <see cref="IntroSkipperDatabase.RebindItemsAsync"/>
    /// has the rules for what moves.
    /// </remarks>
    /// <param name="season">The resolved season.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when every item that needed a hash got one; <see langword="false"/> when a file could not be read, so an orphan may have gone unmatched.</returns>
    public async Task<bool> RebindAsync(ResolvedSeason season, CancellationToken cancellationToken)
    {
        var episodes = season.Episodes.Where(e => e.Path.Length > 0 && !e.IsExcluded).ToList();
        if (episodes.Count == 0)
        {
            return true;
        }

        var episodeIds = episodes.Select(e => e.EpisodeId).ToArray();
        var stored = await database.GetFileHashesAsync(episodeIds, cancellationToken).ConfigureAwait(false);
        var hashes = new Dictionary<Guid, string>();
        var changed = new List<DbFileHash>();
        var allHashed = true;
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
                allHashed = false;
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
            return allHashed;
        }

        // Only a season made wholly of one gone season's files takes that season's state: a
        // stray file moved into an established season must not change how it analyzes.
        Guid? previousSeasonId = rebinds.Count == episodes.Count
            && rebinds.Select(r => r.Orphan.SeasonId).Distinct().ToList() is [var only]
            && only != season.Key
            && isGone(only) ? only : null;
        await database.RebindItemsAsync(rebinds, season.Key, previousSeasonId, cancellationToken).ConfigureAwait(false);
        LogRebound(logger, rebinds.Count, season.Key);
        return allHashed;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not hash the file of {Name} ({ItemId})")]
    private static partial void LogHashFailed(ILogger logger, string name, Guid itemId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Matched {Count} renamed or moved files to their new items in season {SeasonKey}")]
    private static partial void LogRebound(ILogger logger, int count, Guid seasonKey);
}
