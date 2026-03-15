// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

namespace IntroSkipper.Providers
{
    /// <summary>
    /// Introskipper media segment provider.
    /// </summary>
    public class SegmentProvider : IMediaSegmentProvider
    {
        /// <summary>
        /// Mappings between AnalysisMode and MediaSegmentType.
        /// </summary>
        private static readonly Dictionary<AnalysisMode, MediaSegmentType> _segmentMappings = new()
        {
            [AnalysisMode.Introduction] = MediaSegmentType.Intro,
            [AnalysisMode.Recap] = MediaSegmentType.Recap,
            [AnalysisMode.Preview] = MediaSegmentType.Preview,
            [AnalysisMode.Credits] = MediaSegmentType.Outro,
            [AnalysisMode.Commercial] = MediaSegmentType.Commercial
        };

        /// <inheritdoc/>
        public string Name => Plugin.Instance!.Name;

        /// <inheritdoc/>
        public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(Plugin.Instance);

            var segments = new List<MediaSegmentDto>();
            var itemSegments = await Plugin.Instance.GetTimestampsAsync(request.ItemId, cancellationToken).ConfigureAwait(false);

            foreach (var (mode, type) in _segmentMappings)
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

            return segments;
        }

        /// <inheritdoc/>
        public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode or Movie);
    }
}
