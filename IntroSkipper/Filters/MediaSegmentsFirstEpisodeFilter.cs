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
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<MediaSegmentsFirstEpisodeFilter> _logger = logger;

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        _logger.LogDebug("MediaSegments filter invoked. Path: {Path}", context.HttpContext.Request.Path.Value);

        if (!IsMediaSegmentsRequest(context))
        {
            _logger.LogDebug("Request is not MediaSegments. Skipping filter.");
            await next().ConfigureAwait(false);
            return;
        }

        if (!TryGetItemId(context, out var itemId))
        {
            _logger.LogWarning("MediaSegments request missing item id. Route: {RouteValues}", context.RouteData.Values);
            await next().ConfigureAwait(false);
            return;
        }

        _logger.LogDebug("MediaSegments request item id: {ItemId}", itemId);

        if (_libraryManager.GetItemById(itemId) is not Episode episode)
        {
            _logger.LogDebug("Item {ItemId} is not an episode. Skipping filter.", itemId);
            await next().ConfigureAwait(false);
            return;
        }

        if (!IsFirstEpisode(episode))
        {
            _logger.LogDebug("Episode {EpisodeId} is not the first episode. Skipping filter.", episode.Id);
            await next().ConfigureAwait(false);
            return;
        }

        _logger.LogDebug("Filtering intro segments for first episode {EpisodeId} (SeasonId: {SeasonId}, Index: {Index})", episode.Id, episode.SeasonId, episode.IndexNumber);

        if (context.Result is ObjectResult objectResult)
        {
            _logger.LogDebug("Filtering ObjectResult media segments for {EpisodeId}.", episode.Id);
            objectResult.Value = FilterIntroSegments(objectResult.Value);
        }
        else if (context.Result is JsonResult jsonResult)
        {
            _logger.LogDebug("Filtering JsonResult media segments for {EpisodeId}.", episode.Id);
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
            _logger.LogDebug("SkipFirstEpisode disabled in config. Not filtering.");
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

    private static bool IsMediaSegmentsRequest(ResultExecutingContext context)
    {
        if (context.RouteData.Values.TryGetValue("controller", out var controller)
            && controller is not null
            && controller.ToString()!.Contains("MediaSegments", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (context.ActionDescriptor.DisplayName?.Contains("MediaSegments", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        var path = context.HttpContext.Request.Path.Value;
        return path?.Contains("/MediaSegments", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryGetItemId(ResultExecutingContext context, out Guid itemId)
    {
        if (TryParseRouteValue(context, "itemId", out itemId)
            || TryParseRouteValue(context, "id", out itemId)
            || TryParseRouteValue(context, "ItemId", out itemId))
        {
            return true;
        }

        var query = context.HttpContext.Request.Query;
        if (query.TryGetValue("itemId", out var itemIdValues)
            && Guid.TryParse(itemIdValues.FirstOrDefault(), out itemId))
        {
            return true;
        }

        itemId = Guid.Empty;
        return false;
    }

    private static bool TryParseRouteValue(ResultExecutingContext context, string key, out Guid itemId)
    {
        if (context.RouteData.Values.TryGetValue(key, out var value)
            && value is not null
            && Guid.TryParse(value.ToString(), out itemId))
        {
            return true;
        }

        itemId = Guid.Empty;
        return false;
    }

    private object? FilterIntroSegments(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is QueryResult<MediaSegmentDto> queryResult)
        {
            _logger.LogDebug("Filtering QueryResult media segments. Count: {Count}", queryResult.Items?.Count ?? 0);
            var items = queryResult.Items
                ?.Where(segment => segment.Type != MediaSegmentType.Intro)
                .ToArray();

            _logger.LogDebug("Filtered QueryResult media segments. Count: {Count}", items?.Length ?? 0);

            return new QueryResult<MediaSegmentDto>
            {
                Items = items ?? Array.Empty<MediaSegmentDto>(),
                StartIndex = queryResult.StartIndex,
                TotalRecordCount = queryResult.TotalRecordCount
            };
        }

        if (value is IEnumerable<MediaSegmentDto> segments)
        {
            var segmentList = segments.ToList();
            _logger.LogDebug("Filtering list media segments. Count: {Count}", segmentList.Count);
            return segmentList
                .Where(segment => segment.Type != MediaSegmentType.Intro)
                .ToList();
        }

        _logger.LogDebug("Media segments response was not a list of media segments. Type: {Type}", value.GetType().FullName);
        return value;
    }
}
