// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Xunit;

namespace IntroSkipper.Tests;

public class TestTimeRanges
{
    /// <summary>
    /// Tests that TimeRange intersections are detected correctly.
    /// Tests each time range against a range of 5 to 10 seconds.
    /// </summary>
    [Theory]
    [InlineData(1, 4, false)]   // too early
    [InlineData(4, 6, true)]    // intersects on the left
    [InlineData(7, 8, true)]    // in the middle
    [InlineData(9, 12, true)]   // intersects on the right
    [InlineData(13, 15, false)] // too late
    [InlineData(6, 8, true)]    // fully inside
    [InlineData(3, 12, true)]   // fully contains the range
    [InlineData(5, 10, true)]   // identical range
    [InlineData(0, 5, false)]   // touches the start boundary only
    [InlineData(10, 15, false)] // touches the end boundary only
    public void TestTimeRangeIntersection(int start, int end, bool expected)
    {
        var large = new TimeRange(5, 10);
        var testRange = new TimeRange(start, end);

        Assert.Equal(expected, large.Intersects(testRange));
    }
}
