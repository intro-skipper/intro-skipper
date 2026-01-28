// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Services;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.DependencyInjection;

namespace IntroSkipper.Providers
{
    /// <summary>
    /// Introskipper media segment provider.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="SegmentProvider"/> class.
    /// </remarks>
    /// <param name="serviceProvider">The service provider used to resolve dependencies.</param>
    public class SegmentProvider(IServiceProvider serviceProvider) : IMediaSegmentProvider
    {
        private readonly IServiceProvider _serviceProvider = serviceProvider;

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

            using var scope = _serviceProvider.CreateScope();
            var segmentService = scope.ServiceProvider.GetRequiredService<ISegmentService>();

            var segments = new List<MediaSegmentDto>();
            var dbSegments = await segmentService.GetSegmentsAsync(request.ItemId, cancellationToken).ConfigureAwait(false);

            // Convert all segments to DTOs
            foreach (var dbSegment in dbSegments.Where(s => s.ToSegment().Valid))
            {
                if (_segmentMappings.TryGetValue(dbSegment.Type, out var type))
                {
                    long startTicks = (long)(dbSegment.Start * TimeSpan.TicksPerSecond);
                    long endTicks = (long)(dbSegment.End * TimeSpan.TicksPerSecond);

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
