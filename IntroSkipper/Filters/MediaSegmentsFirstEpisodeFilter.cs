// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
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
public sealed class MediaSegmentsFirstEpisodeFilter : IAsyncResultFilter
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediaSegmentsFirstEpisodeFilter> _logger;
    private readonly PluginConfiguration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentsFirstEpisodeFilter"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public MediaSegmentsFirstEpisodeFilter(
        ILibraryManager libraryManager,
        ILogger<MediaSegmentsFirstEpisodeFilter> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (!IsMediaSegmentsRequest(context) || !TryGetItemId(context, out var itemId))
        {
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

        if (context.Result is ObjectResult objectResult)
        {
            objectResult.Value = FilterIntroSegments(objectResult.Value);
        }
        else if (context.Result is JsonResult jsonResult)
        {
            jsonResult.Value = FilterIntroSegments(jsonResult.Value);
        }

        await next().ConfigureAwait(false);
    }

    private bool IsFirstEpisode(Episode episode)
        => _config.SkipFirstEpisode
            && episode.IndexNumber.HasValue
            && episode.IndexNumber.Value == 1;

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
            var items = queryResult.Items
                ?.Where(segment => !string.Equals(segment.Type.ToString(), "Intro", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return new QueryResult<MediaSegmentDto>
            {
                Items = items ?? Array.Empty<MediaSegmentDto>(),
                StartIndex = queryResult.StartIndex,
                TotalRecordCount = items?.Length ?? 0
            };
        }

        if (value is IEnumerable<MediaSegmentDto> segments)
        {
            return segments
                .Where(segment => !string.Equals(segment.Type.ToString(), "Intro", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        _logger.LogDebug("Media segments response was not a list of media segments. Type: {Type}", value.GetType().FullName);
        return value;
    }
}
