// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Helper;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
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
public sealed partial class MediaSegmentsFirstEpisodeFilter(
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
            LogMissingItemId(_logger, context.RouteData.Values);
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

        LogFilteringIntroSegments(_logger, episode.Id, episode.SeasonId, episode.IndexNumber);

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
            LogResultTypeNotRecognized(_logger, context.Result?.GetType().FullName);
        }

        await next().ConfigureAwait(false);
    }

    private bool IsFirstEpisode(Episode episode)
    {
        LogEvaluatingFirstEpisode(_logger, episode.Id, episode.SeasonId, episode.IndexNumber);

        if (Plugin.Instance?.Configuration?.SkipFirstEpisode != true)
        {
            return false;
        }

        if (episode.SeasonId == Guid.Empty)
        {
            LogEpisodeMissingSeasonId(_logger, episode.Id);
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
            LogNoFirstEpisodeFound(_logger, episode.SeasonId);
            return false;
        }

        LogSeasonFirstEpisode(_logger, episode.SeasonId, firstEpisode.Id, episode.Id);

        return firstEpisode.Id == episode.Id;
    }

    private bool IsFilteredEpisode(Episode episode)
    {
        // When anime restriction is disabled or not explicitly enabled, filter all series
        if (Plugin.Instance?.Configuration.SkipFirstEpisodeAnime != true)
        {
            return true;
        }

        // When anime restriction is enabled, only filter anime series
        return episode.Series is Series series &&
            SeriesHelper.IsAnime(series);
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
            LogFilteringQueryResult(_logger, queryResult.Items?.Count ?? 0, items.Length);

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
            LogFilteringListSegments(_logger, filtered.Length);
            return filtered.ToList();
        }

        LogSegmentsResponseNotList(_logger, value.GetType().FullName ?? "null");
        return value;
    }

    private static MediaSegmentDto[] FilterSegments(IEnumerable<MediaSegmentDto>? segments)
    {
        return segments is null
            ? []
            : [.. segments.Where(segment => segment.Type != MediaSegmentType.Intro)];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MediaSegments request missing item id. Route: {RouteValues}")]
    private static partial void LogMissingItemId(ILogger logger, object routeValues);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Filtering intro segments for first episode {EpisodeId} (SeasonId: {SeasonId}, Index: {Index})")]
    private static partial void LogFilteringIntroSegments(ILogger logger, Guid episodeId, Guid seasonId, int? index);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MediaSegments result type not recognized: {ResultType}")]
    private static partial void LogResultTypeNotRecognized(ILogger logger, string? resultType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Evaluating first-episode status for {EpisodeId} (SeasonId: {SeasonId}, Index: {Index})")]
    private static partial void LogEvaluatingFirstEpisode(ILogger logger, Guid episodeId, Guid seasonId, int? index);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Episode {EpisodeId} has no SeasonId. Not filtering.")]
    private static partial void LogEpisodeMissingSeasonId(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No first episode found for SeasonId {SeasonId}. Not filtering.")]
    private static partial void LogNoFirstEpisodeFound(ILogger logger, Guid seasonId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Season {SeasonId} first episode is {FirstEpisodeId}. Current episode is {EpisodeId}.")]
    private static partial void LogSeasonFirstEpisode(ILogger logger, Guid seasonId, Guid firstEpisodeId, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Filtering QueryResult media segments. Before: {Before}, After: {After}")]
    private static partial void LogFilteringQueryResult(ILogger logger, int before, int after);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Filtering list media segments. After: {Count}")]
    private static partial void LogFilteringListSegments(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Media segments response was not a list of media segments. Type: {Type}")]
    private static partial void LogSegmentsResponseNotList(ILogger logger, string type);
}
