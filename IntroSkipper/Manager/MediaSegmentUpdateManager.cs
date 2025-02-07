// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Providers;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Model;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentUpdateManager" /> class.
    /// </summary>
    /// <param name="mediaSegmentManager">MediaSegmentManager.</param>
    /// <param name="logger">logger.</param>
    public class MediaSegmentUpdateManager(IMediaSegmentManager mediaSegmentManager, ILogger<MediaSegmentUpdateManager> logger)
    {
        private const int DefaultMaxParallelism = 3;
        private readonly IMediaSegmentManager _mediaSegmentManager = mediaSegmentManager;
        private readonly ILogger<MediaSegmentUpdateManager> _logger = logger;
        private readonly SegmentProvider _segmentProvider = new();
        private readonly string _id = Plugin.Instance!.Name.ToLowerInvariant()
                        .GetMD5()
                        .ToString("N", CultureInfo.InvariantCulture);

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
            await Parallel.ForEachAsync(
                episodes,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = DefaultMaxParallelism
                },
                async (episode, ct) =>
                {
                    try
                    {
                        // Retrieve the existing segments for the episode.
                        var existingSegments = await _mediaSegmentManager
                            .GetSegmentsAsync(episode.EpisodeId, null, false)
                            .ConfigureAwait(false);

                        // Start deletion of existing segments concurrently.
                        var deleteTask = Task.WhenAll(
                            existingSegments.Select(segment => _mediaSegmentManager.DeleteSegmentAsync(segment.Id)));

                        // Start generating new segments concurrently.
                        var newSegmentsTask = _segmentProvider
                            .GetMediaSegments(new MediaSegmentGenerationRequest { ItemId = episode.EpisodeId }, ct);

                        // Await both deletion and generation concurrently.
                        await Task.WhenAll(deleteTask, newSegmentsTask).ConfigureAwait(false);

                        // Check cancellation before proceeding.
                        ct.ThrowIfCancellationRequested();

                        // Retrieve the newly generated segments.
                        var newSegments = await newSegmentsTask.ConfigureAwait(false);

                        if (newSegments.Count == 0)
                        {
                            _logger.LogDebug("No segments found for episode {EpisodeId}", episode.EpisodeId);
                            return;
                        }

                        // Insert the new segments concurrently.
                        await Task.WhenAll(
                            newSegments.Select(segment => _mediaSegmentManager.CreateSegmentAsync(segment, _id)))
                            .ConfigureAwait(false);

                        _logger.LogDebug("Updated {SegmentCount} segments for episode {EpisodeId}", newSegments.Count, episode.EpisodeId);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug("Processing for episode {EpisodeId} was canceled.", episode.EpisodeId);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing episode {EpisodeId}", episode.EpisodeId);
                    }
                }).ConfigureAwait(false);
        }
    }
}
