// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

/// <summary>
/// Jellyfin library items shaped so the season resolver can enumerate them: identity,
/// series and season keys, index numbers, a media path and a real (non-virtual) location.
/// </summary>
internal static class JellyfinItems
{
    public static Series Series(Guid id, string name = "Series")
    {
        var series = new Series { Name = name };
        EntrypointTestHelpers.SetPropertyOrField(series, "Id", id);
        return series;
    }

    public static Season Season(Guid id, Guid seriesId, int? number = 1, bool isVirtual = false)
    {
        var season = new Season
        {
            SeriesId = seriesId,
            IndexNumber = number,
            IsVirtualItem = isVirtual,
        };
        EntrypointTestHelpers.SetPropertyOrField(season, "Id", id);
        return season;
    }

    public static Episode Episode(
        Guid id,
        Guid seriesId,
        Guid seasonId,
        string seriesName = "Series",
        string name = "Pilot",
        string path = "/media/series/s01e01.mkv",
        int? seasonNumber = 1,
        int episodeNumber = 1)
    {
        var episode = new Episode
        {
            Name = name,
            SeriesId = seriesId,
            SeasonId = seasonId,
            ParentIndexNumber = seasonNumber,
            IndexNumber = episodeNumber,
            Path = path,
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        };
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", id);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeriesName", seriesName);
        EntrypointTestHelpers.EnsureLocationTypeResolvable();
        return episode;
    }

    public static Movie Movie(Guid id, string name = "Feature", string path = "/media/feature.mkv")
    {
        var movie = new Movie
        {
            Id = id,
            Name = name,
            Path = path,
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        };
        EntrypointTestHelpers.EnsureLocationTypeResolvable();
        return movie;
    }

    public static VirtualFolderInfo Folder(string name)
        => new() { Name = name, ItemId = Guid.NewGuid().ToString() };

    /// <summary>
    /// Adds the series and season each episode names, once each, so the resolver finds
    /// the episode through its series and its season key resolves. Movies and any items
    /// already present pass through unchanged.
    /// </summary>
    public static BaseItem[] WithParents(params BaseItem[] items)
    {
        var known = items.Select(item => item.Id).ToHashSet();
        List<BaseItem> result = [.. items];
        foreach (var episode in items.OfType<Episode>())
        {
            if (episode.SeriesId != Guid.Empty && known.Add(episode.SeriesId))
            {
                result.Add(Series(episode.SeriesId, episode.SeriesName));
            }

            if (episode.SeasonId != Guid.Empty && known.Add(episode.SeasonId))
            {
                result.Add(Season(episode.SeasonId, episode.SeriesId, episode.ParentIndexNumber));
            }
        }

        return [.. result];
    }
}
