// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Filters;

/// <summary>
/// Filters media segment responses to remove intro segments for episodes where the intro pattern first appeared.
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

        if (!await IsFirstAppearanceEpisodeAsync(episode.Id).ConfigureAwait(false))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (!IsFilteredEpisode(episode))
        {
            await next().ConfigureAwait(false);
            return;
        }

        _logger.LogDebug("Filtering intro segments for first appearance episode {EpisodeId}", episode.Id);

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

    /// <summary>
    /// Checks if this episode has an intro segment marked as first appearance.
    /// </summary>
    /// <param name="episodeId">The episode ID to check.</param>
    /// <returns>True if this episode has an intro segment with IsFirstAppearance set to true.</returns>
    private async Task<bool> IsFirstAppearanceEpisodeAsync(Guid episodeId)
    {
        if (Plugin.Instance?.Configuration?.SkipFirstEpisode != true)
        {
            return false;
        }

        var dbPath = Plugin.Instance?.DbPath;
        if (string.IsNullOrEmpty(dbPath))
        {
            _logger.LogDebug("Database path not available. Not filtering.");
            return false;
        }

        try
        {
            using var db = new IntroSkipperDbContext(dbPath);
            var hasFirstAppearance = await db.DbSegment
                .AnyAsync(s => s.ItemId == episodeId &&
                               s.Type == AnalysisMode.Introduction &&
                               s.IsFirstAppearance)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Episode {EpisodeId} IsFirstAppearance check: {Result}",
                episodeId,
                hasFirstAppearance);

            return hasFirstAppearance;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking IsFirstAppearance for episode {EpisodeId}", episodeId);
            return false;
        }
    }

    private bool IsFilteredEpisode(Episode episode)
    {
        // When anime restriction is disabled or not explicitly enabled, filter all series
        if (Plugin.Instance?.Configuration.SkipFirstEpisodeAnime != true)
        {
            return true;
        }

        // When anime restriction is enabled, only filter anime series
        return episode.Series is MediaBrowser.Controller.Entities.TV.Series series &&
            (series.Tags.Contains("anime", StringComparison.OrdinalIgnoreCase) ||
            series.Genres.Contains("anime", StringComparison.OrdinalIgnoreCase));
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
