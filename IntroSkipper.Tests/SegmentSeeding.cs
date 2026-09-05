// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.SegmentChanges;
using Xunit;

/// <summary>
/// Seeds and mutates user state through <see cref="IIntroSkipperDatabase.ApplyChangeAsync"/>,
/// the path every production user write takes. Each call journals the item's
/// projection marker, and a delete also clears the item's analysis record for the
/// mode, exactly as the interactive surfaces do; a test asserting on the journal
/// clears it after seeding.
/// </summary>
internal static class SegmentSeeding
{
    /// <summary>Adds a user segment (promoting an exact-range occupant in place) and returns the stored row.</summary>
    public static async Task<DbSegment> SeedUserSegmentAsync(this IIntroSkipperDatabase database, Guid itemId, AnalysisMode mode, long startTicks, long endTicks)
    {
        var value = Assert.Single(await CommitAsync(database, new AddUserSegmentIntent(itemId, mode, startTicks, endTicks)));
        return await RowAsync(database, itemId, value.Id);
    }

    /// <summary>Replaces the mode's active segments with the given user ranges.</summary>
    public static Task SeedUserSegmentsForModeAsync(this IIntroSkipperDatabase database, Guid itemId, AnalysisMode mode, params (long StartTicks, long EndTicks)[] ranges)
        => CommitAsync(database, new ReplaceUserSegmentsForModeIntent(itemId, mode, [.. ranges.Select(r => new SegmentRange(r.StartTicks, r.EndTicks))]));

    /// <summary>Sets whether the item's automatic segments are withheld from Jellyfin; idempotent in both directions.</summary>
    public static Task SetItemDisabledAsync(this IIntroSkipperDatabase database, Guid seasonId, Guid itemId, bool disabled)
        => CommitAsync(database, new SegmentVisibilityChangeIntent(itemId, seasonId, Visible: !disabled));

    /// <summary>
    /// Deletes a segment (automatic rows tombstone, user rows go for good). Returns the
    /// deleted value, or <see langword="null"/> when the id is unknown on the item or
    /// already suppressed.
    /// </summary>
    public static async Task<SegmentValue?> DeleteSegmentAsync(this IIntroSkipperDatabase database, Guid itemId, Guid segmentId)
    {
        var result = await database.ApplyChangeAsync(new DeleteSegmentIntent(itemId, segmentId));
        return result.Outcome is null ? Assert.Single(result.Affected) : null;
    }

    /// <summary>Clears a tombstone. Returns the restored row, or <see langword="null"/> when the id is unknown on the item or not suppressed.</summary>
    public static async Task<DbSegment?> RestoreSegmentAsync(this IIntroSkipperDatabase database, Guid itemId, Guid segmentId)
    {
        var result = await database.ApplyChangeAsync(new RestoreSegmentIntent(itemId, segmentId));
        return result.Outcome is null ? await RowAsync(database, itemId, Assert.Single(result.Affected).Id) : null;
    }

    /// <summary>
    /// Moves a segment's boundaries, promoting the surviving row to user provenance.
    /// Returns the survivor, or <see langword="null"/> when the id is unknown on the
    /// item or suppressed.
    /// </summary>
    public static async Task<DbSegment?> UpdateSegmentAsync(this IIntroSkipperDatabase database, Guid itemId, Guid segmentId, long startTicks, long endTicks)
    {
        var result = await database.ApplyChangeAsync(new UpdateSegmentIntent(itemId, segmentId, startTicks, endTicks));
        return result.Outcome is null ? await RowAsync(database, itemId, Assert.Single(result.Affected).Id) : null;
    }

    // An intent used for seeding must not be rejected; an Ignored outcome (the state
    // already held) counts as seeded and reports the held values.
    private static async Task<IReadOnlyList<SegmentValue>> CommitAsync(IIntroSkipperDatabase database, SegmentChangeIntent intent)
    {
        var result = await database.ApplyChangeAsync(intent);
        if (result.Outcome is Rejected rejected)
        {
            Assert.Fail($"{intent.GetType().Name} was rejected ({rejected.Reason}): {rejected.Message}");
        }

        return result.Outcome is Ignored ignored ? ignored.AffectedValues : result.Affected;
    }

    private static async Task<DbSegment> RowAsync(IIntroSkipperDatabase database, Guid itemId, Guid segmentId)
        => (await database.GetSegmentsAsync(itemId, includeSuppressed: true)).Single(s => s.Id == segmentId);
}
