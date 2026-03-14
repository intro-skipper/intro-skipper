// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Initializes a new instance of the <see cref="MediaSegmentUpdateManager" /> class.
/// </summary>
/// <param name="mediaSegmentManager">The Jellyfin <see cref="IMediaSegmentManager"/> used to update segments.</param>
/// <param name="logger">Application logger.</param>
public partial class MediaSegmentUpdateManager(
    IMediaSegmentManager mediaSegmentManager,
    ILogger<MediaSegmentUpdateManager> logger)
{
    private readonly IMediaSegmentManager _mediaSegmentManager = mediaSegmentManager;
    private readonly ILogger<MediaSegmentUpdateManager> _logger = logger;
    private readonly LibraryOptions _externalProviders = new()
    {
        DisabledMediaSegmentProviders = ["Chapter Segments Provider"]
    };

    /// <summary>
    /// Updates all media items in a List.
    /// </summary>
    /// <param name="episodes">Queued media items.</param>
    /// <param name="cancellationToken">CancellationToken.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task UpdateMediaSegmentsAsync(
        IReadOnlyList<QueuedEpisode> episodes,
        CancellationToken cancellationToken)
    {
        var maxParallelism = Plugin.Instance!.Configuration.MaxParallelism;
        await Parallel.ForEachAsync(
            episodes,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxParallelism
            },
            async (episode, ct) =>
            {
                try
                {
                    // Retrieve the existing segments for the episode.
                    var item = Plugin.Instance!.GetItem(episode.EpisodeId);
                    if (item is null)
                    {
                        LogItemNotFound(_logger, episode.EpisodeId);
                        return;
                    }

                    await _mediaSegmentManager.RunSegmentPluginProviders(item, _externalProviders, true, ct).ConfigureAwait(false);

                    LogUpdatedSegments(_logger, episode.EpisodeId);
                }
                catch (OperationCanceledException)
                {
                    LogProcessingCanceled(_logger, episode.EpisodeId);
                    throw;
                }
                catch (Exception ex)
                {
                    LogErrorProcessingEpisode(_logger, ex, episode.EpisodeId);
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a segment.
    /// </summary>
    /// <param name="segmentId">The Id of the segment.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task DeleteSegmentAsync(Guid segmentId)
    {
        await _mediaSegmentManager.DeleteSegmentAsync(segmentId).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves a segment from Jellyfin by id.
    /// </summary>
    /// <param name="itemId">The item id that owns the segment.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="segmentType">Optional segment type to limit the search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching segment, or <c>null</c> if not found.</returns>
    public async Task<MediaSegmentDto?> GetSegmentAsync(
        Guid itemId,
        Guid segmentId,
        MediaSegmentType? segmentType,
        CancellationToken cancellationToken)
    {
        var item = Plugin.Instance?.GetItem(itemId);
        if (item is null)
        {
            LogItemNotFound(_logger, itemId);
            return null;
        }

        var typeFilter = segmentType.HasValue ? new[] { segmentType.Value } : null;
        var segments = await _mediaSegmentManager
            .GetSegmentsAsync(item, typeFilter, _externalProviders, filterByProvider: false)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return segments.FirstOrDefault(segment => segment.Id == segmentId);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Item not found for episode {EpisodeId}")]
    private static partial void LogItemNotFound(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated segments for episode {EpisodeId}")]
    private static partial void LogUpdatedSegments(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing for episode {EpisodeId} was canceled.")]
    private static partial void LogProcessingCanceled(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing episode {EpisodeId}")]
    private static partial void LogErrorProcessingEpisode(ILogger logger, Exception ex, Guid episodeId);
}
