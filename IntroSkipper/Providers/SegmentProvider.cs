// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Providers
{
    /// <summary>
    /// Introskipper media segment provider.
    /// </summary>
    public class SegmentProvider(ILogger<SegmentProvider> logger) : IMediaSegmentProvider
    {
        private readonly ILogger<SegmentProvider> _logger = logger;

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
            var itemSegments = await Plugin.Instance.GetSegmentsAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
            var (invalidModes, duplicateModes) = Plugin.GetSegmentValidationIssues(itemSegments);
            if (invalidModes.Length > 0 || duplicateModes.Length > 0)
            {
                _logger.LogWarning(
                    "Segment integrity issues detected for item {ItemId}. Invalid modes: {InvalidModes}. Duplicate non-commercial modes: {DuplicateModes}.",
                    request.ItemId,
                    invalidModes.Length > 0 ? string.Join(", ", invalidModes) : "none",
                    duplicateModes.Length > 0 ? string.Join(", ", duplicateModes) : "none");
            }

            var seenNonCommercial = new HashSet<AnalysisMode>();
            foreach (var segment in itemSegments.OrderBy(segment => segment.Start))
            {
                if (!_segmentMappings.TryGetValue(segment.Type, out var type))
                {
                    continue;
                }

                if (!Plugin.IsSegmentRangeValid(segment))
                {
                    continue;
                }

                if (segment.Type != AnalysisMode.Commercial && !seenNonCommercial.Add(segment.Type))
                {
                    continue;
                }

                long startTicks = Plugin.RoundToTicks(segment.Start);
                long endTicks = Plugin.RoundToTicks(segment.End);

                segments.Add(new MediaSegmentDto
                {
                    StartTicks = startTicks,
                    EndTicks = endTicks,
                    ItemId = request.ItemId,
                    Type = type
                });
            }

            return segments;
        }

        /// <inheritdoc/>
        public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode or Movie);
    }
}
