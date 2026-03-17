// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
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
    /// Creates a Jellyfin media segment for the given item, replacing any existing Jellyfin
    /// segment of the same non-commercial type first.  For commercial segments the new entry
    /// is appended without touching existing ones (multiple per item are intentional).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is deliberately type-scoped: it only deletes / creates segments of the
    /// one <see cref="MediaSegmentType"/> being saved.  That makes it safe to call
    /// concurrently for different segment types on the same item, which is exactly what
    /// clients such as segment-editor-mobile do when they fire one HTTP POST per segment in
    /// parallel.
    /// </para>
    /// <para>
    /// The previous implementation used
    /// <see cref="UpdateMediaSegmentsAsync"/> (which calls
    /// <c>RunSegmentPluginProviders(forceOverwrite: true)</c>).  That approach deletes
    /// <em>all</em> Jellyfin segments for the item and re-adds <em>all</em> segments from
    /// the plugin DB, so concurrent calls from the same save operation raced and produced
    /// one duplicate per segment.
    /// </para>
    /// </remarks>
    /// <param name="item">The media item that owns the segment.</param>
    /// <param name="segment">The segment DTO to persist in Jellyfin's database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task CreateOrReplaceSegmentAsync(BaseItem item, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        // Resolve the provider entry for "Intro Skipper" so we can pass its stable ID to
        // Jellyfin's CreateSegmentAsync, keeping segment attribution correct.
        var providerEntry = _mediaSegmentManager
            .GetSupportedProviders(item)
            .FirstOrDefault(p => string.Equals(p.Name, Plugin.Instance!.Name, StringComparison.OrdinalIgnoreCase));

        if (providerEntry == default)
        {
            LogProviderNotFound(_logger, item.Id);
            return;
        }

        if (segment.Type != MediaSegmentType.Commercial)
        {
            // Delete any stale Jellyfin segment of the same type before creating the new
            // one.  Scoping the delete to this type avoids the race that occurred when
            // RunSegmentPluginProviders(forceOverwrite=true) was used: that deleted every
            // type and then re-added everything, so two concurrent calls (e.g. Intro + Credits
            // in parallel) each saw the other's newly-added segments and added them again.
            // filterByProvider: true ensures we only touch segments from providers that are
            // active for this library, leaving segments from other providers intact.
            var existingSegments = await _mediaSegmentManager
                .GetSegmentsAsync(item, [segment.Type], _externalProviders, filterByProvider: true)
                .ConfigureAwait(false);

            foreach (var existing in existingSegments)
            {
                await _mediaSegmentManager.DeleteSegmentAsync(existing.Id).ConfigureAwait(false);
            }
        }

        segment.ItemId = item.Id;
        await _mediaSegmentManager.CreateSegmentAsync(segment, providerEntry.Id).ConfigureAwait(false);
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching segment, or <c>null</c> if not found.</returns>
    public async Task<MediaSegmentDto?> GetSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        var item = Plugin.Instance?.GetItem(itemId);
        if (item is null)
        {
            LogItemNotFound(_logger, itemId);
            return null;
        }

        var segments = await _mediaSegmentManager
            .GetSegmentsAsync(item, null, _externalProviders, filterByProvider: false)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return segments.FirstOrDefault(segment => segment.Id == segmentId);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Item not found for episode {EpisodeId}")]
    private static partial void LogItemNotFound(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Intro Skipper provider entry not found for item {ItemId}; Jellyfin segment will not be created")]
    private static partial void LogProviderNotFound(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated segments for episode {EpisodeId}")]
    private static partial void LogUpdatedSegments(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing for episode {EpisodeId} was canceled.")]
    private static partial void LogProcessingCanceled(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing episode {EpisodeId}")]
    private static partial void LogErrorProcessingEpisode(ILogger logger, Exception ex, Guid episodeId);
}
