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
/// Being the single source is also what enforces per-item disable: filtering
/// automatic segments of disabled items here covers every push and pull path.
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
    /// Every active segment maps to one DTO carrying the plugin row's id, so the
    /// Jellyfin row and the plugin row share the same Guid.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The converted segments, ordered by type and start time.</returns>
    public async Task<IReadOnlyList<MediaSegmentDto>> CreateAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var segments = new List<MediaSegmentDto>();
        var isDisabled = await _database.IsItemDisabledAsync(itemId, cancellationToken).ConfigureAwait(false);
        var itemSegments = await _database.GetSegmentsAsync(itemId, cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var segment in itemSegments)
        {
            // Disabled items keep analyzing and storing segments; only explicitly
            // user-provided rows may reach Jellyfin.
            if (isDisabled && !segment.IsUserProvided)
            {
                continue;
            }

            if (!AnalysisHelpers.ModeToSegmentType.TryGetValue(segment.Type, out var type))
            {
                continue;
            }

            if (segment.EndTicks <= segment.StartTicks)
            {
                continue;
            }

            segments.Add(new MediaSegmentDto
            {
                Id = segment.Id,
                StartTicks = segment.StartTicks,
                EndTicks = segment.EndTicks,
                ItemId = itemId,
                Type = type
            });
        }

        return segments;
    }
}
