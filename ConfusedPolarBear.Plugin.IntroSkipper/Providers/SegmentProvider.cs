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

namespace ConfusedPolarBear.Plugin.IntroSkipper.Providers
{
    /// <summary>
    /// Introskipper media segment provider.
    /// </summary>
    public class SegmentProvider : IMediaSegmentProvider
    {
        private readonly int _remainingSecondsOfIntro;

        /// <summary>
        /// Initializes a new instance of the <see cref="SegmentProvider"/> class.
        /// </summary>
        public SegmentProvider()
        {
            _remainingSecondsOfIntro = Plugin.Instance?.Configuration.RemainingSecondsOfIntro ?? 2;
        }

        /// <inheritdoc/>
        public string Name => Plugin.Instance!.Name;

        /// <inheritdoc/>
        public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
        {
            var segments = new List<MediaSegmentDto>();

            if (Plugin.Instance!.Intros.TryGetValue(request.ItemId, out var introValue))
            {
                segments.Add(new MediaSegmentDto
                {
                    StartTicks = TimeSpan.FromSeconds(introValue.Start).Ticks,
                    EndTicks = TimeSpan.FromSeconds(introValue.End - _remainingSecondsOfIntro).Ticks,
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Intro
                });
            }

            if (Plugin.Instance!.Credits.TryGetValue(request.ItemId, out var creditValue))
            {
                segments.Add(new MediaSegmentDto
                {
                    StartTicks = TimeSpan.FromSeconds(creditValue.Start).Ticks,
                    EndTicks = TimeSpan.FromSeconds(creditValue.End - _remainingSecondsOfIntro).Ticks,
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Outro
                });
            }

            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(segments);
        }

        /// <inheritdoc/>
        public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode);
    }
}
