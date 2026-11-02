// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Filters;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestMediaSegmentsFirstEpisodeFilter
{
    [Theory]
    [InlineData(false, 1, 1, true)]
    [InlineData(false, 2, 1, true)]
    [InlineData(false, 1, 2, false)]
    [InlineData(true, 1, 1, true)]
    public async Task Ignore_RemovesIntroOnlyFromTheSeasonPremiere(bool seasonFolders, int season, int episode, bool introRemoved)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { FirstEpisodeIntroMode = PluginConfiguration.FirstEpisodeIntroModeIgnore });
        var seriesId = Guid.NewGuid();
        var seasonIds = new Dictionary<int, Guid> { [1] = Guid.NewGuid(), [2] = Guid.NewGuid() };
        List<BaseItem> items = [JellyfinItems.Series(seriesId), .. seasonIds.Select(pair => JellyfinItems.Season(pair.Value, seriesId, pair.Key))];

        // The fake does not sort, so episodes are listed in index order.
        foreach (var (seasonNumber, episodeNumber) in new[] { (1, 1), (1, 2), (2, 1) })
        {
            var item = JellyfinItems.Episode(Guid.NewGuid(), seriesId, seasonIds[seasonNumber], seasonNumber: seasonNumber, episodeNumber: episodeNumber);
            item.ParentId = seasonFolders ? seasonIds[seasonNumber] : seriesId;
            items.Add(item);
        }

        var requested = items.OfType<Episode>()
            .Single(item => item.ParentIndexNumber == season && item.IndexNumber == episode);
        var filter = new MediaSegmentsFirstEpisodeFilter(
            EntrypointTestHelpers.FakeLibraryManager.Create([], items),
            NullLogger<MediaSegmentsFirstEpisodeFilter>.Instance);
        var routeData = new RouteData();
        routeData.Values["itemId"] = requested.Id.ToString();
        var result = new ObjectResult(new QueryResult<MediaSegmentDto>(
        [
            new MediaSegmentDto { ItemId = requested.Id, Type = MediaSegmentType.Intro },
            new MediaSegmentDto { ItemId = requested.Id, Type = MediaSegmentType.Outro },
        ]));
        var context = new ResultExecutingContext(
            new ActionContext(new DefaultHttpContext(), routeData, new ActionDescriptor()),
            [],
            result,
            controller: new object());

        await filter.OnResultExecutionAsync(
            context,
            () => Task.FromResult(new ResultExecutedContext(context, [], context.Result, context.Controller)));

        var served = Assert.IsType<QueryResult<MediaSegmentDto>>(result.Value);
        MediaSegmentType[] expected = introRemoved ? [MediaSegmentType.Outro] : [MediaSegmentType.Intro, MediaSegmentType.Outro];
        Assert.Equal(expected, served.Items.Select(segment => segment.Type));
    }
}
