// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;
using FakeLibraryManager = EntrypointTestHelpers.FakeLibraryManager;

/// <summary>
/// Resolution of library items into seasons. The fake library models Jellyfin's ancestor
/// chain with seasons and episodes under their series and nothing under a season, so an
/// episode is only reachable through its series, as it is for a flat series folder.
/// </summary>
public sealed class TestSeasonResolver
{
    [Fact]
    public void ResolveLibrary_GroupsEpisodesBySeasonAndMoviesByTheirOwnId()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var firstSeasonId = Guid.NewGuid();
        var secondSeasonId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var episodes = new[]
        {
            JellyfinItems.Episode(Guid.NewGuid(), seriesId, firstSeasonId, name: "S01E01"),
            JellyfinItems.Episode(Guid.NewGuid(), seriesId, secondSeasonId, name: "S02E01", seasonNumber: 2),
            JellyfinItems.Episode(Guid.NewGuid(), seriesId, secondSeasonId, name: "S02E02", seasonNumber: 2, episodeNumber: 2),
        };
        // The fake answers in list order where Jellyfin would sort by name, so the
        // series is listed ahead of the movie.
        var resolver = CreateResolver(JellyfinItems.WithParents([JellyfinItems.Series(seriesId), .. episodes, JellyfinItems.Movie(movieId)]));

        var (seasons, failures) = resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None);

        Assert.Equal(0, failures);
        Assert.Equal([firstSeasonId, secondSeasonId, movieId], seasons.Select(season => season.Key));

        var first = Assert.Single(seasons[0].Episodes);
        Assert.Equal(episodes[0].Id, first.EpisodeId);
        Assert.Equal(seriesId, first.SeriesId);
        Assert.Equal(firstSeasonId, first.SeasonId);
        Assert.Equal(1, first.SeasonNumber);
        Assert.Equal("Series", first.SeriesName);
        Assert.Equal("S01E01", first.Name);
        Assert.Equal(QueuedMediaCategory.Episode, first.Category);
        Assert.Equal(240, first.Duration);
        Assert.False(first.IsExcluded);
        Assert.Null(first.FileVersion);

        Assert.Equal(["S02E01", "S02E02"], seasons[1].Episodes.Select(episode => episode.Name));

        var movie = Assert.Single(seasons[2].Episodes);
        Assert.Equal(movieId, movie.EpisodeId);
        Assert.Equal(movieId, movie.SeriesId);
        Assert.Equal(movieId, movie.SeasonId);
        Assert.Equal(QueuedMediaCategory.Movie, movie.Category);
    }

    [Fact]
    public void Resolve_QueuesAnItemReturnedTwiceOnce()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var episode = JellyfinItems.Episode(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var items = JellyfinItems.WithParents(episode);

        // GetItemList has returned the same item twice; queued twice, the episode
        // would be fingerprint-matched against itself.
        var resolver = CreateResolver([.. items, episode]);

        var season = Assert.Single(resolver.Resolve(items.Single(item => item is Series)));

        Assert.Equal(episode.Id, Assert.Single(season.Episodes).EpisodeId);
    }

    [Fact]
    public void Resolve_GroupsInSeasonSpecialWithItsHostSeason()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var hostSeasonId = Guid.NewGuid();
        var specialsSeasonId = Guid.NewGuid();
        var host = JellyfinItems.Episode(Guid.NewGuid(), seriesId, hostSeasonId, name: "S01E01");
        var inSeasonSpecial = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId, name: "Airs before S01", seasonNumber: 0);
        inSeasonSpecial.AirsBeforeSeasonNumber = 1;
        var plainSpecial = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId, name: "Special", seasonNumber: 0, episodeNumber: 2);
        var resolver = CreateResolver(JellyfinItems.WithParents(host, inSeasonSpecial, plainSpecial));

        var seasons = resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None).Seasons;

        var hostSeason = Assert.Single(seasons, season => season.Key == hostSeasonId);
        Assert.Equal([host.Id, inSeasonSpecial.Id], hostSeason.Episodes.Select(episode => episode.EpisodeId));
        Assert.All(hostSeason.Episodes, episode => Assert.Equal(hostSeasonId, episode.SeasonId));

        var specials = Assert.Single(seasons, season => season.Key == specialsSeasonId);
        Assert.Equal(plainSpecial.Id, Assert.Single(specials.Episodes).EpisodeId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_KeepsInSeasonSpecialInSpecials_WhenHostSeasonIsVirtualOrAbsent(bool hostIsVirtual)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var specialsSeasonId = Guid.NewGuid();
        var special = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId, seasonNumber: 0);
        special.AirsAfterSeasonNumber = 3;
        List<BaseItem> items = [.. JellyfinItems.WithParents(special)];
        if (hostIsVirtual)
        {
            items.Add(JellyfinItems.Season(Guid.NewGuid(), seriesId, number: 3, isVirtual: true));
        }

        var resolver = CreateResolver(items);

        var season = Assert.Single(resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None).Seasons);

        Assert.Equal(specialsSeasonId, season.Key);
        Assert.Equal(special.Id, Assert.Single(season.Episodes).EpisodeId);
    }

    [Fact]
    public void Resolve_KeysSeasonUnknownEpisodesUnderThatSeason()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var unknownSeasonId = Guid.NewGuid();
        var episode = JellyfinItems.Episode(Guid.NewGuid(), seriesId, unknownSeasonId, seasonNumber: null);
        var resolver = CreateResolver(JellyfinItems.WithParents(episode));

        var season = Assert.Single(resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None).Seasons);

        Assert.Equal(unknownSeasonId, season.Key);
        var queued = Assert.Single(season.Episodes);
        Assert.Equal(0, queued.SeasonNumber);
    }

    [Fact]
    public void Resolve_KeysEpisodeWithoutSeasonIdWithItsNumberedSiblings_ElseByItsOwnId()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var secondSeasonId = Guid.NewGuid();
        var sibling = JellyfinItems.Episode(Guid.NewGuid(), seriesId, secondSeasonId, seasonNumber: 2);
        var numbered = JellyfinItems.Episode(Guid.NewGuid(), seriesId, Guid.Empty, seasonNumber: 2, episodeNumber: 2);
        var unnumbered = JellyfinItems.Episode(Guid.NewGuid(), seriesId, Guid.Empty, seasonNumber: 5);
        var resolver = CreateResolver(JellyfinItems.WithParents(sibling, numbered, unnumbered));

        var seasons = resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None).Seasons;

        Assert.Equal([secondSeasonId, unnumbered.Id], seasons.Select(season => season.Key));
        Assert.Equal([sibling.Id, numbered.Id], seasons[0].Episodes.Select(episode => episode.EpisodeId));
    }

    [Fact]
    public void Resolve_KeepsInSeasonSpecialInSpecials_WhenHostSeasonIsExcluded()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { PathExclusions = { "/media/series/season-3" } });
        var seriesId = Guid.NewGuid();
        var specialsSeasonId = Guid.NewGuid();
        var host = JellyfinItems.Episode(Guid.NewGuid(), seriesId, Guid.NewGuid(), path: "/media/series/season-3/s03e01.mkv", seasonNumber: 3);
        var special = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId, seasonNumber: 0);
        special.AirsAfterSeasonNumber = 3;
        var resolver = CreateResolver(JellyfinItems.WithParents(host, special));

        // Joining the excluded host would leave the special a season of one with nothing
        // to compare against. In Specials it is compared with the other specials.
        var season = Assert.Single(resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None).Seasons);

        Assert.Equal(specialsSeasonId, season.Key);
        Assert.Equal(special.Id, Assert.Single(season.Episodes).EpisodeId);
    }

    [Fact]
    public void Resolve_HostsAnInSeasonSpecialWithTheSeasonOfItsFirstEpisode_WhenTwoSeasonsShareANumber()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var firstFolderId = Guid.NewGuid();
        var secondFolderId = Guid.NewGuid();
        var first = JellyfinItems.Episode(Guid.NewGuid(), seriesId, firstFolderId, name: "S01E01");
        var second = JellyfinItems.Episode(Guid.NewGuid(), seriesId, secondFolderId, name: "S01E02", episodeNumber: 2);
        var special = JellyfinItems.Episode(Guid.NewGuid(), seriesId, Guid.NewGuid(), seasonNumber: 0);
        special.AirsBeforeSeasonNumber = 1;
        var resolver = CreateResolver(JellyfinItems.WithParents(first, second, special));

        var seasons = resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None).Seasons;

        // Jellyfin keeps one Season item per folder, "Season 1" and "Season 01" here. The
        // special joins the one holding the lowest-numbered episode, on every run.
        Assert.Equal([first.Id, special.Id], Assert.Single(seasons, season => season.Key == firstFolderId).Episodes.Select(episode => episode.EpisodeId));
    }

    [Fact]
    public void SeasonKey_AgreesWithResolution()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var hostSeasonId = Guid.NewGuid();
        var specialsSeasonId = Guid.NewGuid();
        var regular = JellyfinItems.Episode(Guid.NewGuid(), seriesId, hostSeasonId);
        var hosted = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId, seasonNumber: 0);
        hosted.AirsBeforeSeasonNumber = 1;
        var unhosted = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId, seasonNumber: 0, episodeNumber: 2);
        unhosted.AirsBeforeSeasonNumber = 9;
        var movie = JellyfinItems.Movie(Guid.NewGuid());
        var resolver = CreateResolver(JellyfinItems.WithParents(regular, hosted, unhosted, movie));

        var keysByEpisode = resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None).Seasons
            .SelectMany(season => season.Episodes.Select(episode => (episode.EpisodeId, season.Key)))
            .ToDictionary();

        Assert.Equal(hostSeasonId, resolver.SeasonKey(regular));
        Assert.Equal(hostSeasonId, resolver.SeasonKey(hosted));
        Assert.Equal(specialsSeasonId, resolver.SeasonKey(unhosted));
        Assert.Equal(movie.Id, resolver.SeasonKey(movie));
        Assert.All(new BaseItem[] { regular, hosted, unhosted, movie }, item => Assert.Equal(keysByEpisode[item.Id], resolver.SeasonKey(item)));
    }

    [Fact]
    public void ResolveKey_ResolvesMoviesAndSeasons_AndAnswersNullForUnknownIds()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var emptySeasonId = Guid.NewGuid();
        var episode = JellyfinItems.Episode(Guid.NewGuid(), seriesId, seasonId);
        var orphan = JellyfinItems.Episode(Guid.NewGuid(), seriesId, Guid.Empty, seasonNumber: 7);
        var movie = JellyfinItems.Movie(Guid.NewGuid());
        var resolver = CreateResolver([.. JellyfinItems.WithParents(episode, orphan, movie), JellyfinItems.Season(emptySeasonId, seriesId, number: 2)]);

        var season = resolver.ResolveKey(seasonId);
        Assert.NotNull(season);
        Assert.Equal(seriesId, season.SeriesId);
        Assert.Equal(episode.Id, Assert.Single(season.Episodes).EpisodeId);

        var movieSeason = resolver.ResolveKey(movie.Id);
        Assert.NotNull(movieSeason);
        Assert.Equal(movie.Id, movieSeason.SeriesId);
        Assert.Equal(movie.Id, Assert.Single(movieSeason.Episodes).EpisodeId);

        // A known season with nothing to analyze is an answer, not a missing season.
        var emptySeason = resolver.ResolveKey(emptySeasonId);
        Assert.NotNull(emptySeason);
        Assert.Equal(seriesId, emptySeason.SeriesId);
        Assert.Empty(emptySeason.Episodes);
        Assert.True(resolver.IsKnownKey(emptySeasonId));

        // An episode that is its own key (no season, no numbered siblings) is one too.
        var orphanSeason = resolver.ResolveKey(orphan.Id);
        Assert.NotNull(orphanSeason);
        Assert.Equal(orphan.Id, Assert.Single(orphanSeason.Episodes).EpisodeId);
        Assert.True(resolver.IsKnownKey(orphan.Id));

        Assert.Null(resolver.ResolveKey(Guid.NewGuid()));
        Assert.Null(resolver.ResolveKey(episode.Id));
        Assert.False(resolver.IsKnownKey(episode.Id));
    }

    [Fact]
    public void OwnersOf_AnswersTheSeriesOrMovieOfEachId_AndDropsUnknownIdsAndOptedOutLibraries()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episode = JellyfinItems.Episode(Guid.NewGuid(), seriesId, seasonId);
        var movie = JellyfinItems.Movie(Guid.NewGuid());
        var items = JellyfinItems.WithParents(episode, movie);
        var resolver = CreateResolver(items);

        Assert.Equal([seriesId, movie.Id], resolver.OwnersOf([episode.Id, seasonId, movie.Id, Guid.NewGuid()]).Select(owner => owner.Id));

        // Opting the library out under Media Segment Providers hides its items from the
        // watcher and the dashboard alike, as it does from a full run.
        var optedOut = new LibraryOptions { DisabledMediaSegmentProviders = [Plugin.Instance!.Name] };
        var optedOutResolver = EntrypointTestHelpers.CreateSeasonResolver(FakeLibraryManager.Create([JellyfinItems.Folder("Media")], items, optedOut));

        Assert.Empty(optedOutResolver.OwnersOf([episode.Id, seasonId, movie.Id]));
        Assert.Null(optedOutResolver.ResolveKey(seasonId));
        Assert.False(optedOutResolver.IsKnownKey(movie.Id));
    }

    [Fact]
    public void Resolve_DropsExcludedItems_UnlessAskedToFlagThem()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { SeriesExclusions = { "Excluded Show" } });
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var excluded = JellyfinItems.Episode(Guid.NewGuid(), seriesId, seasonId, seriesName: "Excluded Show");
        var resolver = CreateResolver(JellyfinItems.WithParents(excluded, JellyfinItems.Movie(Guid.NewGuid())));

        var dropped = resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None).Seasons;
        var flagged = resolver.ResolveLibrary(includeExcluded: true, CancellationToken.None).Seasons;

        Assert.DoesNotContain(dropped, season => season.Key == seasonId);
        var season = Assert.Single(flagged, season => season.Key == seasonId);
        Assert.True(Assert.Single(season.Episodes).IsExcluded);
    }

    [Fact]
    public void EnumerateLibrary_SkipsProviderDisabledLibraries_AndCountsInvalidFolderIds()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var disabledFolder = JellyfinItems.Folder("Anime");
        disabledFolder.LibraryOptions = new LibraryOptions { DisabledMediaSegmentProviders = [Plugin.Instance!.Name] };
        var brokenFolder = new VirtualFolderInfo { Name = "Broken", ItemId = "not-a-guid" };
        var movie = JellyfinItems.Movie(Guid.NewGuid());
        var libraryManager = FakeLibraryManager.Create([JellyfinItems.Folder("Movies"), disabledFolder, brokenFolder], [movie]);
        var resolver = EntrypointTestHelpers.CreateSeasonResolver(libraryManager);

        var (seasons, failures) = resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None);

        Assert.Equal(1, failures);
        Assert.Equal(movie.Id, Assert.Single(seasons).Key);
    }

    [Fact]
    public void ResolveLibrary_CountsAThrowingLibraryAndAThrowingSeries()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var moviesFolder = JellyfinItems.Folder("Movies");
        var movie = JellyfinItems.Movie(Guid.NewGuid());
        var series = JellyfinItems.Series(Guid.NewGuid());
        var libraryManager = FakeLibraryManager.Create(
            [moviesFolder, JellyfinItems.Folder("Shows")],
            query => query switch
            {
                { AncestorIds.Length: > 0 } => throw new InvalidOperationException("series unavailable"),
                { ParentId: var parentId } when parentId == Guid.Parse(moviesFolder.ItemId!) => [movie, series],
                _ => throw new InvalidOperationException("library database unavailable"),
            });
        var resolver = EntrypointTestHelpers.CreateSeasonResolver(libraryManager);

        var (seasons, failures) = resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None);

        Assert.Equal(2, failures);
        Assert.Equal(movie.Id, Assert.Single(seasons).Key);
    }

    [Fact]
    public void Resolve_MarksAnimeSeriesAndRecordsTheFileVersion()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var episode = JellyfinItems.Episode(Guid.NewGuid(), seriesId, Guid.NewGuid());
        episode.DateModified = new DateTime(2, DateTimeKind.Utc);
        var series = JellyfinItems.Series(seriesId);
        series.Genres = ["Anime"];
        var resolver = CreateResolver([.. JellyfinItems.WithParents(episode).Where(item => item.Id != seriesId), series]);

        var queued = Assert.Single(Assert.Single(resolver.Resolve(series)).Episodes);

        Assert.Equal(QueuedMediaCategory.AnimeEpisode, queued.Category);
        Assert.Equal(2, queued.FileVersion);
    }

    private static SeasonResolver CreateResolver(IReadOnlyList<BaseItem> items)
        => EntrypointTestHelpers.CreateSeasonResolver(FakeLibraryManager.Create([JellyfinItems.Folder("Media")], items));
}
