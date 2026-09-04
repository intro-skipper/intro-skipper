// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

/// <summary>
/// Jellyfin library items shaped so the queue manager can enumerate them: identity
/// and season keys, index numbers, a media path and a real (non-virtual) location.
/// </summary>
internal static class JellyfinItems
{
    public static Episode Episode(Guid id, Guid seriesId, Guid seasonId, string seriesName = "Series", string name = "Pilot", string path = "/media/series/s01e01.mkv")
    {
        var episode = new Episode
        {
            Name = name,
            SeriesId = seriesId,
            SeasonId = seasonId,
            ParentIndexNumber = 1,
            IndexNumber = 1,
            Path = path,
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        };
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", id);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeriesName", seriesName);
        EntrypointTestHelpers.EnsureNonVirtual(episode);
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
        EntrypointTestHelpers.EnsureNonVirtual(movie);
        return movie;
    }

    public static VirtualFolderInfo Folder(string name)
        => new() { Name = name, ItemId = Guid.NewGuid().ToString() };
}
