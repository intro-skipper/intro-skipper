using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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
        /// <inheritdoc/>
        public string Name => Plugin.Instance!.Name;

        /// <inheritdoc/>
        public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var segments = new List<MediaSegmentDto>();
            var remainingTicks = TimeSpan.FromSeconds(Plugin.Instance?.Configuration.RemainingSecondsOfIntro ?? 2).Ticks;

            if (Plugin.Instance!.Intros.TryGetValue(request.ItemId, out var introValue) && introValue.Valid)
            {
                segments.Add(new MediaSegmentDto
                {
                    StartTicks = TimeSpan.FromSeconds(introValue.Start).Ticks,
                    EndTicks = TimeSpan.FromSeconds(introValue.End).Ticks - remainingTicks,
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Intro
                });
            }

            if (Plugin.Instance!.Credits.TryGetValue(request.ItemId, out var creditValue) && creditValue.Valid)
            {
                var creditEndTicks = TimeSpan.FromSeconds(creditValue.End).Ticks;

                if (Plugin.Instance.GetItem(request.ItemId) is not null and var item &&
                    item.RunTimeTicks - TimeSpan.TicksPerSecond < creditEndTicks)
                {
                    creditEndTicks = item.RunTimeTicks ?? creditEndTicks;
                }
                else
                {
                    creditEndTicks -= remainingTicks;
                }

                segments.Add(new MediaSegmentDto
                {
                    StartTicks = TimeSpan.FromSeconds(creditValue.Start).Ticks,
                    EndTicks = creditEndTicks,
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Outro
                });
            }

            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(segments);
        }

        /// <inheritdoc/>
        public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode or Movie);
    }
}
