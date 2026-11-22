// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Services;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FakeLibraryManager = EntrypointTestHelpers.FakeLibraryManager;

public sealed class TestPreviewSettingMigration : IDisposable
{
    private readonly TempSegmentDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Migration_StoresEachSeasonsLegacyChoiceAndCompletes()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { AnimePreviewFromCreditsEnd = true });
        var anime = new SeriesFixture(anime: true);
        var plain = new SeriesFixture(anime: false);
        var chosenSeasonId = Guid.NewGuid();
        var chosen = JellyfinItems.Episode(Guid.NewGuid(), anime.SeriesId, chosenSeasonId, path: "/media/anime/s02e01.mkv", seasonNumber: 2);
        await _db.Database.SetAnalysisOverridesAsync(chosenSeasonId, null, null, false, null, null);

        await RunMigrationAsync([.. anime.Items, .. plain.Items, chosen]);

        Assert.True((await _db.Database.GetAnalysisOverridesAsync(anime.SeasonId)).PreviewFromCreditsEnd);
        Assert.False((await _db.Database.GetAnalysisOverridesAsync(plain.SeasonId)).PreviewFromCreditsEnd);
        Assert.False((await _db.Database.GetAnalysisOverridesAsync(chosenSeasonId)).PreviewFromCreditsEnd);
        Assert.True(await _db.Database.IsPreviewSettingMigrationCompleteAsync());
    }

    [Fact]
    public async Task Migration_UnresolvedItem_StoresNothingAndStaysPending()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { AnimePreviewFromCreditsEnd = true });
        var anime = new SeriesFixture(anime: true);
        var pathless = JellyfinItems.Episode(Guid.NewGuid(), anime.SeriesId, anime.SeasonId, path: string.Empty, episodeNumber: 2);

        await RunMigrationAsync([.. anime.Items, pathless]);

        Assert.False(await _db.Database.IsPreviewSettingMigrationCompleteAsync());
        Assert.Null((await _db.Database.GetAnalysisOverridesAsync(anime.SeasonId)).PreviewFromCreditsEnd);
        await using var db = _db.Context();
        Assert.Empty(await db.SeasonAnalysisOverrides.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Migration_SwitchOff_CompletesDespiteAnUnresolvedItem()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { AnimePreviewFromCreditsEnd = false });
        var anime = new SeriesFixture(anime: true);
        var pathless = JellyfinItems.Episode(Guid.NewGuid(), anime.SeriesId, anime.SeasonId, path: string.Empty, episodeNumber: 2);

        await RunMigrationAsync([.. anime.Items, pathless]);

        Assert.True(await _db.Database.IsPreviewSettingMigrationCompleteAsync());
        Assert.False((await _db.Database.GetAnalysisOverridesAsync(anime.SeasonId)).PreviewFromCreditsEnd);
    }

    private async Task RunMigrationAsync(IReadOnlyList<BaseItem> items)
    {
        var resolver = EntrypointTestHelpers.CreateSeasonResolver(
            FakeLibraryManager.Create([JellyfinItems.Folder("Media")], JellyfinItems.WithParents([.. items])));
        using var migration = new PreviewSettingMigration(resolver, _db.Database, NullLogger<PreviewSettingMigration>.Instance);
        await migration.StartAsync(CancellationToken.None);
        await migration.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class SeriesFixture
    {
        public SeriesFixture(bool anime)
        {
            var series = JellyfinItems.Series(SeriesId, anime ? "Anime" : "Plain");
            if (anime)
            {
                series.Genres = ["Anime"];
            }

            var episode = JellyfinItems.Episode(Guid.NewGuid(), SeriesId, SeasonId, seriesName: series.Name, path: $"/media/{series.Name}/s01e01.mkv");
            Items = [series, episode];
        }

        public Guid SeriesId { get; } = Guid.NewGuid();

        public Guid SeasonId { get; } = Guid.NewGuid();

        public BaseItem[] Items { get; }
    }
}
