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
            var runTimeTicks = Plugin.Instance.GetItem(request.ItemId)?.RunTimeTicks ?? 0;

            // Define mappings between AnalysisMode and MediaSegmentType
            var segmentMappings = new List<(AnalysisMode Mode, MediaSegmentType Type)>
            {
                (AnalysisMode.Introduction, MediaSegmentType.Intro),
                (AnalysisMode.Recap, MediaSegmentType.Recap),
                (AnalysisMode.Preview, MediaSegmentType.Preview),
                (AnalysisMode.Credits, MediaSegmentType.Outro)
            };

            foreach (var (mode, type) in segmentMappings)
            {
                if (itemSegments.TryGetValue(mode, out var segment) && segment.Valid)
                {
                    long startTicks = (long)(segment.Start * TimeSpan.TicksPerSecond);
                    long endTicks = CalculateEndTicks(mode, segment, runTimeTicks, remainingTicks);

                    segments.Add(new MediaSegmentDto
                    {
                        StartTicks = startTicks,
                        EndTicks = endTicks,
                        ItemId = request.ItemId,
                        Type = type
                    });
                }
            }

            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(segments);
        }

        /// <summary>
        /// Calculates the end ticks based on the segment type and runtime.
        /// </summary>
        private static long CalculateEndTicks(AnalysisMode mode, Segment segment, long runTimeTicks, long remainingTicks)
        {
            long endTicks = (long)(segment.End * TimeSpan.TicksPerSecond);

            if (mode is AnalysisMode.Preview or AnalysisMode.Credits)
            {
                if (runTimeTicks > 0 && runTimeTicks < endTicks + TimeSpan.TicksPerSecond)
                {
                    return Math.Max(runTimeTicks, endTicks);
                }

                return endTicks - remainingTicks;
            }

            return endTicks - remainingTicks;
        }

        /// <inheritdoc/>
        public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode or Movie);
    }
}
