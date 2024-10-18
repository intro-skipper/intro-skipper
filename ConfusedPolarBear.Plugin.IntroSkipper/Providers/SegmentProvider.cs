using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Providers
{
    /// <summary>
    /// Introskipper media segment provider.
    /// </summary>
    public class SegmentProvider : IMediaSegmentProvider
    {
        private readonly long _remainingTicks;

        /// <summary>
        /// Initializes a new instance of the <see cref="SegmentProvider"/> class.
        /// </summary>
        public SegmentProvider()
        {
            _remainingTicks = TimeSpan.FromSeconds(Plugin.Instance?.Configuration.RemainingSecondsOfIntro ?? 2).Ticks;
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
                    EndTicks = TimeSpan.FromSeconds(introValue.End).Ticks - _remainingTicks,
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Intro
                });
            }

            if (Plugin.Instance!.Credits.TryGetValue(request.ItemId, out var creditValue))
            {
                var outroSegment = new MediaSegmentDto
                {
                    StartTicks = TimeSpan.FromSeconds(creditValue.Start).Ticks,
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Outro
                };

                var creditEndTicks = TimeSpan.FromSeconds(creditValue.End).Ticks;

                if (Plugin.Instance.GetItem(request.ItemId) is IHasMediaSources item && creditEndTicks + TimeSpan.TicksPerSecond >= item.RunTimeTicks)
                {
                    outroSegment.EndTicks = item.RunTimeTicks ?? creditEndTicks;
                }
                else
                {
                    outroSegment.EndTicks = creditEndTicks - _remainingTicks;
                }

                segments.Add(outroSegment);
            }

            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(segments);
        }

        /// <inheritdoc/>
        public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is IHasMediaSources);
    }
}
