// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// Pins the strict-overlap rule that drives every automatic-admission guard in
/// <c>ReplaceAutoSegmentsAsync</c>: touching boundaries must never count as overlap,
/// and a single shared tick must. The facade-level counterparts live in
/// <see cref="TestSegmentTombstones"/> (tombstone axis) and TestDatabaseFacades
/// (user-segment and credits-versus-intro axes).
/// </summary>
public sealed class TestAutoSegmentAdmissionPolicy
{
    [Theory]
    [InlineData(0, 10, 10, 20, false)] // touching end-to-start is not overlap
    [InlineData(0, 10, 11, 20, false)] // disjoint with a gap
    [InlineData(0, 10, 9, 20, true)]   // one tick of shared range
    [InlineData(0, 10, 3, 6, true)]    // containment
    [InlineData(0, 10, 0, 10, true)]   // identical
    [InlineData(5, 10, 0, 10, true)]   // shared end, different start
    [InlineData(0, 10, 0, 4, true)]    // shared start, different end
    public void Overlaps_IsStrictAtBoundaries_AndSymmetric(long aStart, long aEnd, long bStart, long bEnd, bool expected)
    {
        Assert.Equal(expected, AutoSegmentAdmissionPolicy.Overlaps(aStart, aEnd, bStart, bEnd));

        // The guard compares incoming/stored ranges in one fixed argument order; the
        // predicate must not care which side is which.
        Assert.Equal(expected, AutoSegmentAdmissionPolicy.Overlaps(bStart, bEnd, aStart, aEnd));
    }
}
