// SPDX-FileCopyrightText: 2024 TwistedUmbrellaX
// SPDX-FileCopyrightText: 2024-2025 rlauuzo
// SPDX-FileCopyrightText: 2024 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

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
            var itemSegments = Plugin.Instance.GetTimestamps(request.ItemId);

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
                    long endTicks = (long)(segment.End * TimeSpan.TicksPerSecond);

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

        /// <inheritdoc/>
        public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode or Movie);
    }
}
