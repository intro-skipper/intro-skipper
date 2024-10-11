using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Providers
{
    /// <summary>
    /// Introskipper media segment provider.
    /// </summary>
    public class IntroskipperSegmentProvider : IMediaSegmentProvider
    {
        private readonly ILogger<IntroskipperSegmentProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="IntroskipperSegmentProvider"/> class.
        /// </summary>
        /// <param name="logger">Instance of the <see cref="ILogger{IntroskipperSegmentProvider}"/> interface.</param>
        public IntroskipperSegmentProvider(ILogger<IntroskipperSegmentProvider> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc/>
        public string Name => Plugin.Instance!.Name;

        /// <inheritdoc/>
        public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Getting media segments for item {ItemId}", request.ItemId);

            var segments = new List<MediaSegmentDto>();

            if (Plugin.Instance!.Intros.TryGetValue(request.ItemId, out var introValue))
            {
                _logger.LogDebug("Found intro for item {ItemId}", request.ItemId);
                segments.Add(new MediaSegmentDto
                {
                    StartTicks = TimeSpan.FromSeconds(introValue.Start).Ticks,
                    EndTicks = TimeSpan.FromSeconds(introValue.End).Ticks,
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Intro
                });
            }

            if (Plugin.Instance!.Credits.TryGetValue(request.ItemId, out var creditValue))
            {
                _logger.LogDebug("Found outro for item {ItemId}", request.ItemId);
                segments.Add(new MediaSegmentDto
                {
                    StartTicks = TimeSpan.FromSeconds(creditValue.Start).Ticks,
                    EndTicks = TimeSpan.FromSeconds(creditValue.End).Ticks,
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Outro
                });
            }

            _logger.LogInformation("Found {SegmentCount} segments for item {ItemId}", segments.Count, request.ItemId);
            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(segments);
        }

        /// <inheritdoc/>
        public ValueTask<bool> Supports(BaseItem item)
        {
            var isSupported = item is Episode;
            _logger.LogInformation("Support check for item {ItemId}: {IsSupported}", item.Id, isSupported);
            return ValueTask.FromResult(isSupported);
        }
    }
}
