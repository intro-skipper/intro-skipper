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
/// Reads through the facade's servable-segments query, so the per-item disable
/// policy is applied identically here and on the legacy skip endpoints.
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
        var itemSegments = await _database.GetServableSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);

        // Stored rows always satisfy end > start and carry a mapped mode (every write
        // boundary validates); a violated invariant fails loudly instead of being
        // silently dropped: an unmapped mode throws right here in the indexer, a bad
        // range throws at the Jellyfin write (JellyfinSegmentStore.Map on the push
        // path, the server's own validation on the provider pull path).
        return [.. itemSegments.Select(segment => new MediaSegmentDto
        {
            Id = segment.Id,
            StartTicks = segment.StartTicks,
            EndTicks = segment.EndTicks,
            ItemId = itemId,
            Type = AnalysisHelpers.ModeToSegmentType[segment.Type]
        })];
    }
}
