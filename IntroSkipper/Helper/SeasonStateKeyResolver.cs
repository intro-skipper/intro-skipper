// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace IntroSkipper.Helper;

/// <summary>
/// Resolves the season-state key for an item. Season states are keyed by the analysis
/// queue's season key, which differs from the item's own SeasonId for in-season specials
/// (grouped with the season they air within) and for episodes whose SeasonId could not be
/// resolved. Prefer the queue key when the item is present in the cached queue.
/// </summary>
internal static class SeasonStateKeyResolver
{
    /// <summary>
    /// Resolves the season-state key for an item.
    /// </summary>
    /// <param name="item">The item whose season-state key to resolve.</param>
    /// <returns>The season-state key.</returns>
    internal static Guid Resolve(BaseItem item)
    {
        var queue = Plugin.Instance!.QueuedMediaItems;

        // Nearly every episode is queued under its own season, so check that bucket
        // before falling back to a scan of the whole queue for in-season specials
        // grouped under another season's key.
        if (item is Episode episode
            && queue.TryGetValue(episode.SeasonId, out var seasonEpisodes)
            && seasonEpisodes.Any(e => e.EpisodeId == item.Id))
        {
            return episode.SeasonId;
        }

        // Movies (and any other non-episode) are queued under their own id, so probe
        // that bucket before falling back to the full-queue scan, which only exists for
        // in-season specials grouped under another season's key.
        if (item is not Episode
            && queue.TryGetValue(item.Id, out var ownEntries)
            && ownEntries.Any(e => e.EpisodeId == item.Id))
        {
            return item.Id;
        }

        foreach (var (seasonId, episodes) in queue)
        {
            if (episodes.Any(e => e.EpisodeId == item.Id))
            {
                return seasonId;
            }
        }

        return item is Episode fallbackEpisode ? fallbackEpisode.SeasonId : item.Id;
    }
}
