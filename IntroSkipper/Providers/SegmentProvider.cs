// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
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
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(Plugin.Instance);

            var segments = new List<MediaSegmentDto>();
            var remainingTicks = Plugin.Instance.Configuration.RemainingSecondsOfIntro * TimeSpan.TicksPerSecond;
            var itemSegments = Plugin.Instance.GetSegmentsById(request.ItemId);

            // Add intro segment if found
            if (itemSegments.TryGetValue(AnalysisMode.Introduction, out var introSegment))
            {
                segments.Add(new MediaSegmentDto
                {
                    StartTicks = (long)(introSegment.Start * TimeSpan.TicksPerSecond),
                    EndTicks = (long)(introSegment.End * TimeSpan.TicksPerSecond) - remainingTicks,
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Intro
                });
            }

            // Add outro/credits segment if found
            if (itemSegments.TryGetValue(AnalysisMode.Introduction, out var creditSegment))
            {
                var creditEndTicks = (long)(creditSegment.End * TimeSpan.TicksPerSecond);
                var runTimeTicks = Plugin.Instance.GetItem(request.ItemId)?.RunTimeTicks ?? long.MaxValue;

                segments.Add(new MediaSegmentDto
                {
                    StartTicks = (long)(creditSegment.Start * TimeSpan.TicksPerSecond),
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
