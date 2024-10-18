using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
using ConfusedPolarBear.Plugin.IntroSkipper.Providers;
using Jellyfin.Data.Entities;
using Jellyfin.Server.Implementations;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Manager
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentUpdateManager" /> class.
    /// </summary>
    /// <param name="dbProvider">EFCore Database factory.</param>
    /// <param name="logger">logger.</param>
    public class MediaSegmentUpdateManager(IDbContextFactory<JellyfinDbContext> dbProvider, ILogger<MediaSegmentUpdateManager> logger) : IMediaSegmentUpdateManager
    {
        private readonly IDbContextFactory<JellyfinDbContext> _dbProvider = dbProvider;
        private readonly ILogger<MediaSegmentUpdateManager> _logger = logger;
        private readonly SegmentProvider _segmentProvider = new();
        private readonly string _name = Plugin.Instance!.Name;

        /// <summary>
        /// Updates all media items in a List.
        /// </summary>
        /// <param name="episodes">Queued media items.</param>
        /// <param name="cancellationToken">CancellationToken.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task UpdateMediaSegments(IReadOnlyList<QueuedEpisode> episodes, CancellationToken cancellationToken)
        {
            using var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await db.MediaSegments
                        .Where(e => e.ItemId == episode.EpisodeId)
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

                    var segments = await _segmentProvider.GetMediaSegments(
                        new MediaSegmentGenerationRequest { ItemId = episode.EpisodeId },
                        cancellationToken).ConfigureAwait(false);

                    if (segments.Count == 0)
                    {
                        _logger.LogDebug("No segments found for episode {EpisodeId}", episode.EpisodeId);
                        continue;
                    }

                    var mappedSegments = segments.Select(Map);
                    await db.MediaSegments.AddRangeAsync(mappedSegments, cancellationToken).ConfigureAwait(false);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    _logger.LogDebug("Added {SegmentCount} segments for episode {EpisodeId}", segments.Count, episode.EpisodeId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing episode {EpisodeId}", episode.EpisodeId);
                }
            }
        }

        private MediaSegment Map(MediaSegmentDto segment) => new()
        {
            Id = segment.Id,
            EndTicks = segment.EndTicks,
            ItemId = segment.ItemId,
            StartTicks = segment.StartTicks,
            Type = segment.Type,
            SegmentProviderId = _name
        };
    }
}
