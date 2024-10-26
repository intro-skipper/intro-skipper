// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

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

namespace IntroSkipper.Providers
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
            var segments = new List<MediaSegmentDto>();
            var remainingTicks = (Plugin.Instance?.Configuration.RemainingSecondsOfIntro ?? 2) * TimeSpan.TicksPerSecond;

            if (Plugin.Instance!.Intros.TryGetValue(request.ItemId, out var introValue))
            {
                segments.Add(new MediaSegmentDto
                {
                    StartTicks = (long)(introValue.Start * TimeSpan.TicksPerSecond),
                    EndTicks = (long)(introValue.End * TimeSpan.TicksPerSecond) - remainingTicks,
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Intro
                });
            }

            if (Plugin.Instance.Credits.TryGetValue(request.ItemId, out var creditValue))
            {
                var creditEndTicks = (long)(creditValue.End * TimeSpan.TicksPerSecond);
                var runTimeTicks = Plugin.Instance.GetItem(request.ItemId)?.RunTimeTicks ?? long.MaxValue;
                segments.Add(new MediaSegmentDto
                {
                    StartTicks = (long)(creditValue.Start * TimeSpan.TicksPerSecond),
                    EndTicks = runTimeTicks > creditEndTicks + TimeSpan.TicksPerSecond
                        ? creditEndTicks - remainingTicks
                        : runTimeTicks,
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
