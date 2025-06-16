// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Providers;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentUpdateManager" /> class.
    /// </summary>
    /// <param name="mediaSegmentManager">The Jellyfin <see cref="IMediaSegmentManager"/> used to update segments.</param>
    /// <param name="externalSegmentProviders">Registry providing access to external segment providers to be disabled.</param>
    /// <param name="logger">Application logger.</param>
    public class MediaSegmentUpdateManager(
        IMediaSegmentManager mediaSegmentManager,
        IExternalSegmentProviders externalSegmentProviders,
        ILogger<MediaSegmentUpdateManager> logger)
    {
        private readonly IMediaSegmentManager _mediaSegmentManager = mediaSegmentManager;
        private readonly ILogger<MediaSegmentUpdateManager> _logger = logger;
        private readonly LibraryOptions _libraryOptions = externalSegmentProviders.Providers;

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
            _logger.LogDebug("External segment providers: {Providers}", string.Join(", ", _libraryOptions.DisabledMediaSegmentProviders));

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
                            _logger.LogError("Item not found for episode {EpisodeId}", episode.EpisodeId);
                            return;
                        }

                        await _mediaSegmentManager.RunSegmentPluginProviders(item, _libraryOptions, true, ct).ConfigureAwait(false);

                        _logger.LogDebug("Updated segments for episode {EpisodeId}", episode.EpisodeId);
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
