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
        Assert.All(hostSeason.Episodes, episode =>
        {
            Assert.Equal(hostSeasonId, episode.SeasonId);
            Assert.Equal(1, episode.SeasonNumber);
        });

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
    public void Resolve_KeysEpisodeWithoutSeasonIdWithItsNumberedSiblings_ElseWaitsForJellyfin()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var secondSeasonId = Guid.NewGuid();
        var sibling = JellyfinItems.Episode(Guid.NewGuid(), seriesId, secondSeasonId, seasonNumber: 2);
        var numbered = JellyfinItems.Episode(Guid.NewGuid(), seriesId, Guid.Empty, seasonNumber: 2, episodeNumber: 2);
        var unnumbered = JellyfinItems.Episode(Guid.NewGuid(), seriesId, Guid.Empty, seasonNumber: 5);
        var resolver = CreateResolver(JellyfinItems.WithParents(sibling, numbered, unnumbered));

        var seasons = resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None).Seasons;

        // Analyzed alone, the fifth-season episode would be recorded as analyzed before
        // its season exists. It waits for the series refresh that attaches it, whose save
        // queues it again.
        var season = Assert.Single(seasons);
        Assert.Equal(secondSeasonId, season.Key);
        Assert.Equal([sibling.Id, numbered.Id], season.Episodes.Select(episode => episode.EpisodeId));
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
        // to compare against. In Specials it is compared with the other specials, and it
        // carries the Specials number, not the one it airs after, so the Specials opt-out
        // applies to it.
        var season = Assert.Single(resolver.ResolveLibrary(includeExcluded: false, CancellationToken.None).Seasons);

        Assert.Equal(specialsSeasonId, season.Key);
        var queued = Assert.Single(season.Episodes);
        Assert.Equal(special.Id, queued.EpisodeId);
        Assert.Equal(0, queued.SeasonNumber);
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
    public void ResolveDisplayed_ResolvesMoviesAndSeasons_AndAnswersNullForUnknownIds()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var emptySeasonId = Guid.NewGuid();
        var orphanSeasonId = Guid.NewGuid();
        var episode = JellyfinItems.Episode(Guid.NewGuid(), seriesId, seasonId);
        var movie = JellyfinItems.Movie(Guid.NewGuid());
        var resolver = CreateResolver([
            .. JellyfinItems.WithParents(episode, movie),
            JellyfinItems.Season(emptySeasonId, seriesId, number: 2),
            JellyfinItems.Season(orphanSeasonId, Guid.NewGuid(), number: 3),
        ]);

        var season = resolver.ResolveDisplayed(seasonId);
        Assert.NotNull(season);
        Assert.Equal(seriesId, season.SeriesId);
        Assert.Equal([episode.Id], season.ItemIds);
        Assert.Equal(episode.Id, Assert.Single(season.Episodes).EpisodeId);

        var movieSeason = resolver.ResolveDisplayed(movie.Id);
        Assert.NotNull(movieSeason);
        Assert.Equal(movie.Id, movieSeason.SeriesId);
        Assert.Equal([movie.Id], movieSeason.ItemIds);
        Assert.Equal(movie.Id, Assert.Single(movieSeason.Episodes).EpisodeId);

        // A known season with nothing to show is an answer, not a missing season.
        var emptySeason = resolver.ResolveDisplayed(emptySeasonId);
        Assert.NotNull(emptySeason);
        Assert.Equal(seriesId, emptySeason.SeriesId);
        Assert.Empty(emptySeason.ItemIds);
        Assert.Empty(emptySeason.Episodes);
        Assert.True(resolver.IsKnownKey(emptySeasonId));

        // A season under a series the server does not know is missing to both lookups, so
        // the analyzer actions answer 404 as the season endpoints do.
        Assert.Null(resolver.ResolveDisplayed(orphanSeasonId));
        Assert.False(resolver.IsKnownKey(orphanSeasonId));

        // An episode id is never a season key.
        Assert.Null(resolver.ResolveDisplayed(Guid.NewGuid()));
        Assert.Null(resolver.ResolveDisplayed(episode.Id));
        Assert.False(resolver.IsKnownKey(episode.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveDisplayed_ListsTheEpisodesJellyfinShowsUnderASeason_WithTheKeyEachIsAnalyzedUnder(bool specialsWithinSeasons)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var hostSeasonId = Guid.NewGuid();
        var specialsSeasonId = Guid.NewGuid();
        var regular = JellyfinItems.Episode(Guid.NewGuid(), seriesId, hostSeasonId);
        var hosted = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId, seasonNumber: 0);
        hosted.AirsBeforeSeasonNumber = 1;
        var plain = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId, seasonNumber: 0, episodeNumber: 2);
        var resolver = EntrypointTestHelpers.CreateSeasonResolver(
            FakeLibraryManager.Create([JellyfinItems.Folder("Media")], JellyfinItems.WithParents(regular, hosted, plain)),
            new ServerConfiguration { DisplaySpecialsWithinSeasons = specialsWithinSeasons });

        // Season 0 shows every special, the hosted one with the key of the host season it
        // is analyzed in, so its erase reaches the special and its scan runs the host.
        var specials = resolver.ResolveDisplayed(specialsSeasonId);
        Assert.NotNull(specials);
        Assert.Equal([(hosted.Id, hostSeasonId), (plain.Id, specialsSeasonId)], specials.Episodes.Select(episode => (episode.EpisodeId, episode.SeasonId)));

        // Jellyfin's season view lists the special under its host season too when the
        // server shows specials within seasons, and the host season's erase must reach it there.
        IEnumerable<Guid> shownInHost = specialsWithinSeasons ? [regular.Id, hosted.Id] : [regular.Id];
        Assert.Equal(shownInHost, resolver.ResolveDisplayed(hostSeasonId)!.Episodes.Select(episode => episode.EpisodeId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveDisplayed_ListsIneligibleEpisodesAsShown_ButNotForAnalysis(bool specialsWithinSeasons)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { PathExclusions = { "/media/excluded" } });
        var seriesId = Guid.NewGuid();
        var hostSeasonId = Guid.NewGuid();
        var specialsSeasonId = Guid.NewGuid();
        var regular = JellyfinItems.Episode(Guid.NewGuid(), seriesId, hostSeasonId);
        var excluded = JellyfinItems.Episode(Guid.NewGuid(), seriesId, hostSeasonId, path: "/media/excluded/s01e02.mkv", episodeNumber: 2);
        var pathless = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId, path: string.Empty, seasonNumber: 0);
        pathless.AirsBeforeSeasonNumber = 1;
        var resolver = EntrypointTestHelpers.CreateSeasonResolver(
            FakeLibraryManager.Create([JellyfinItems.Folder("Media")], JellyfinItems.WithParents(regular, excluded, pathless)),
            new ServerConfiguration { DisplaySpecialsWithinSeasons = specialsWithinSeasons });

        // The dashboard lists every episode Jellyfin shows and renders its disable flag,
        // so the shown ids keep the excluded and pathless episodes the analysis drops.
        var host = resolver.ResolveDisplayed(hostSeasonId);
        Assert.NotNull(host);
        IEnumerable<Guid> shownInHost = specialsWithinSeasons ? [regular.Id, excluded.Id, pathless.Id] : [regular.Id, excluded.Id];
        Assert.Equal(shownInHost, host.ItemIds);
        Assert.Equal([regular.Id], host.Episodes.Select(episode => episode.EpisodeId));

        var specials = resolver.ResolveDisplayed(specialsSeasonId);
        Assert.NotNull(specials);
        Assert.Equal([pathless.Id], specials.ItemIds);
        Assert.Empty(specials.Episodes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveDisplayed_ListsVirtualEpisodesAsShown_ButNotForAnalysis(bool specialsWithinSeasons)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var hostSeasonId = Guid.NewGuid();
        var specialsSeasonId = Guid.NewGuid();
        var regular = JellyfinItems.Episode(Guid.NewGuid(), seriesId, hostSeasonId);
        var missing = JellyfinItems.Episode(Guid.NewGuid(), seriesId, hostSeasonId, episodeNumber: 2);
        missing.IsVirtualItem = true;
        var hosted = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId, seasonNumber: 0);
        hosted.IsVirtualItem = true;
        hosted.AirsBeforeSeasonNumber = 1;
        var items = JellyfinItems.WithParents(regular, missing, hosted);
        var library = FakeLibraryManager.Create([JellyfinItems.Folder("Media")], items);
        var queries = 0;
        var resolver = EntrypointTestHelpers.CreateSeasonResolver(
            FakeLibraryManager.Create(
                [JellyfinItems.Folder("Media")],
                query =>
                {
                    queries++;
                    return [.. library.GetItemList(query!, false)];
                },
                id => library.GetItemById(id)),
            new ServerConfiguration { DisplaySpecialsWithinSeasons = specialsWithinSeasons });

        var host = resolver.ResolveDisplayed(hostSeasonId);

        Assert.NotNull(host);
        Assert.Equal(1, queries);
        IEnumerable<Guid> shownInHost = specialsWithinSeasons ? [regular.Id, missing.Id, hosted.Id] : [regular.Id, missing.Id];
        Assert.Equal(shownInHost, host.ItemIds);
        Assert.Equal([regular.Id], host.Episodes.Select(episode => episode.EpisodeId));

        var specials = resolver.ResolveDisplayed(specialsSeasonId);

        Assert.NotNull(specials);
        Assert.Equal(2, queries);
        Assert.Equal([hosted.Id], specials.ItemIds);
        Assert.Empty(specials.Episodes);
    }

    [Fact]
    public void Resolve_VirtualEpisodesCannotSeedAHostSeason()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seriesId = Guid.NewGuid();
        var hostSeasonId = Guid.NewGuid();
        var specialsSeasonId = Guid.NewGuid();
        var missing = JellyfinItems.Episode(Guid.NewGuid(), seriesId, hostSeasonId, seasonNumber: 3);
        missing.IsVirtualItem = true;
        var special = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId, seasonNumber: 0);
        special.AirsAfterSeasonNumber = 3;
        var items = JellyfinItems.WithParents(missing, special);
        var resolver = EntrypointTestHelpers.CreateSeasonResolver(
            FakeLibraryManager.Create([JellyfinItems.Folder("Media")], items),
            new ServerConfiguration { DisplaySpecialsWithinSeasons = false });

        var season = Assert.Single(resolver.Resolve(items.Single(item => item is Series)));

        Assert.Equal(specialsSeasonId, season.Key);
        Assert.Equal(special.Id, Assert.Single(season.Episodes).EpisodeId);
        var displayedHost = resolver.ResolveDisplayed(hostSeasonId);
        Assert.NotNull(displayedHost);
        Assert.Equal([missing.Id], displayedHost.ItemIds);
        Assert.Empty(displayedHost.Episodes);
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
        Assert.Null(optedOutResolver.ResolveDisplayed(seasonId));
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
