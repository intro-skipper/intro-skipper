// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;
using MediaBrowser.Model.MediaSegments;

namespace IntroSkipper.Providers;

/// <summary>
/// Builds Jellyfin media segment DTOs from Intro Skipper's stored segments.
/// Single source of truth for the plugin-to-Jellyfin conversion so that direct
/// database pushes and provider runs produce identical data — which lets
/// Jellyfin's scheduled segment extraction detect "no changes" and skip rewrites.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SegmentDtoFactory"/> class.
/// </remarks>
/// <param name="database">Segment database facade.</param>
public sealed class SegmentDtoFactory(IIntroSkipperDatabase database)
{
    private readonly IIntroSkipperDatabase _database = database;

    /// <summary>
    /// Creates the Jellyfin media segment DTOs for an item from the plugin database.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The converted segments, ordered by start time.</returns>
    public async Task<IReadOnlyList<MediaSegmentDto>> CreateAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var segments = new List<MediaSegmentDto>();
        var itemSegments = await _database.GetSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        var dedupedModes = new HashSet<AnalysisMode>();

        foreach (var segment in itemSegments.OrderBy(static segment => segment.Start))
        {
            if (!AnalysisHelpers.ModeToSegmentType.TryGetValue(segment.Type, out var type))
            {
                continue;
            }

            if (segment.End <= 0.0)
            {
                continue;
            }

            if (segment.Type != AnalysisMode.Commercial && !dedupedModes.Add(segment.Type))
            {
                continue;
            }

            long startTicks = (long)(segment.Start * TimeSpan.TicksPerSecond);
            long endTicks = (long)(segment.End * TimeSpan.TicksPerSecond);

            segments.Add(new MediaSegmentDto
            {
                StartTicks = startTicks,
                EndTicks = endTicks,
                ItemId = itemId,
                Type = type
            });
        }

        return segments;
    }
}
