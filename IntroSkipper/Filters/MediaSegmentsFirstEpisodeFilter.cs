// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Filters;

/// <summary>
/// Filters media segment responses to remove intro segments for season premieres.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MediaSegmentsFirstEpisodeFilter"/> class.
/// </remarks>
/// <param name="libraryManager">Library manager.</param>
/// <param name="logger">Logger.</param>
public sealed class MediaSegmentsFirstEpisodeFilter(
    ILibraryManager libraryManager,
    ILogger<MediaSegmentsFirstEpisodeFilter> logger) : IAsyncResultFilter
{
    private static readonly string[] _routeItemKeys = ["itemId", "id", "ItemId"];
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<MediaSegmentsFirstEpisodeFilter> _logger = logger;

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (!IsMediaSegmentsRequest(context))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (!TryGetItemId(context, out var itemId))
        {
            _logger.LogWarning("MediaSegments request missing item id. Route: {RouteValues}", context.RouteData.Values);
            await next().ConfigureAwait(false);
            return;
        }

        if (_libraryManager.GetItemById(itemId) is not Episode episode)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (!IsFirstEpisode(episode))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (!IsFilteredEpisode(episode))
        {
            await next().ConfigureAwait(false);
            return;
        }

        _logger.LogDebug("Filtering intro segments for first episode {EpisodeId} (SeasonId: {SeasonId}, Index: {Index})", episode.Id, episode.SeasonId, episode.IndexNumber);

        if (context.Result is ObjectResult objectResult)
        {
            objectResult.Value = FilterIntroSegments(objectResult.Value);
        }
        else if (context.Result is JsonResult jsonResult)
        {
            jsonResult.Value = FilterIntroSegments(jsonResult.Value);
        }
        else
        {
            _logger.LogDebug("MediaSegments result type not recognized: {ResultType}", context.Result?.GetType().FullName);
        }

        await next().ConfigureAwait(false);
    }

    private bool IsFirstEpisode(Episode episode)
    {
        _logger.LogDebug("Evaluating first-episode status for {EpisodeId} (SeasonId: {SeasonId}, Index: {Index})", episode.Id, episode.SeasonId, episode.IndexNumber);

        if (Plugin.Instance?.Configuration?.SkipFirstEpisode != true)
        {
            return false;
        }

        if (episode.SeasonId == Guid.Empty)
        {
            _logger.LogDebug("Episode {EpisodeId} has no SeasonId. Not filtering.", episode.Id);
            return false;
        }

        var query = new InternalItemsQuery
        {
            ParentId = episode.SeasonId,
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = false,
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.IndexNumber, SortOrder.Ascending)]
        };

        var firstEpisode = _libraryManager.GetItemList(query, false)
            .OfType<Episode>()
            .FirstOrDefault();

        if (firstEpisode is null)
        {
            _logger.LogDebug("No first episode found for SeasonId {SeasonId}. Not filtering.", episode.SeasonId);
            return false;
        }

        _logger.LogDebug("Season {SeasonId} first episode is {FirstEpisodeId}. Current episode is {EpisodeId}.", episode.SeasonId, firstEpisode.Id, episode.Id);

        return firstEpisode.Id == episode.Id;
    }

    private bool IsFilteredEpisode(Episode episode)
    {
        if (Plugin.Instance?.Configuration.SkipFirstEpisodeAnime != true)
        {
            return true;
        }

        var series = episode.Series;

        return Array.Exists(series.Tags, element => element.Equals("anime", StringComparison.OrdinalIgnoreCase)) ||
            Array.Exists(series.Genres, element => element.Equals("anime", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMediaSegmentsRequest(ResultExecutingContext context)
    {
        static bool ContainsMediaSegments(string? value)
            => value?.Contains("MediaSegments", StringComparison.OrdinalIgnoreCase) == true;

        if (context.RouteData.Values.TryGetValue("controller", out var controller)
            && ContainsMediaSegments(controller?.ToString()))
        {
            return true;
        }

        if (ContainsMediaSegments(context.ActionDescriptor.DisplayName))
        {
            return true;
        }

        var path = context.HttpContext.Request.Path.Value;
        return ContainsMediaSegments(path);
    }

    private static bool TryGetItemId(ResultExecutingContext context, out Guid itemId)
    {
        foreach (var key in _routeItemKeys)
        {
            if (TryParseGuid(context.RouteData.Values.TryGetValue(key, out var value) ? value : null, out itemId))
            {
                return true;
            }
        }

        var queryValue = context.HttpContext.Request.Query["itemId"].FirstOrDefault();
        return Guid.TryParse(queryValue, out itemId);
    }

    private static bool TryParseGuid(object? value, out Guid guid)
        => Guid.TryParse(value?.ToString(), out guid);

    private object? FilterIntroSegments(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is QueryResult<MediaSegmentDto> queryResult)
        {
            var items = FilterSegments(queryResult.Items);
            _logger.LogDebug(
                "Filtering QueryResult media segments. Before: {Before}, After: {After}",
                queryResult.Items?.Count ?? 0,
                items.Length);

            return new QueryResult<MediaSegmentDto>
            {
                Items = items,
                StartIndex = queryResult.StartIndex,
                TotalRecordCount = items?.Length ?? 0
            };
        }

        if (value is IEnumerable<MediaSegmentDto> segments)
        {
            var filtered = FilterSegments(segments);
            _logger.LogDebug("Filtering list media segments. After: {Count}", filtered.Length);
            return filtered.ToList();
        }

        _logger.LogDebug("Media segments response was not a list of media segments. Type: {Type}", value.GetType().FullName);
        return value;
    }

    private static MediaSegmentDto[] FilterSegments(IEnumerable<MediaSegmentDto>? segments)
    {
        return segments?
            .Where(segment => segment.Type != MediaSegmentType.Intro)
            .ToArray()
            ?? [];
    }
}
