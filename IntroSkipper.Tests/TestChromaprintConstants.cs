// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only


using System;
using IntroSkipper.Data;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestChromaprintConstants
{
    [Fact]
    public void SampleDuration_MatchesChromaprintHopDuration()
    {
        // 4096 / 11025 / 3 ≈ 0.12383
        Assert.Equal(ChromaprintConstants.SampleDuration, 4096.0 / 11025.0 / 3.0);
    }

    [Fact]
    public void HashWindowDuration_Is2Point6()
    {
        Assert.Equal(2.6, ChromaprintConstants.HashWindowDuration);
    }

    [Theory]
    [InlineData(4825, 600)]  // 4825 * 0.12383 + 2.6 ≈ 600.1 → rounds to 600
    [InlineData(0, 3)]       // 0 * 0.12383 + 2.6 → rounds to 3
    [InlineData(1, 3)]       // 1 * 0.12383 + 2.6 ≈ 2.72 → rounds to 3
    [InlineData(7250, 900)]  // 7250 * 0.12383 + 2.6 ≈ 900.4 → Math.Round → 900
    public void InferDuration_ReturnsRoundedSeconds(int lineCount, double expected)
    {
        Assert.Equal(expected, ChromaprintConstants.InferDuration(lineCount));
    }

    [Fact]
    public void DurationTolerance_IsFiveSeconds()
    {
        Assert.Equal(5.0, ChromaprintConstants.DurationTolerance);
    }

    [Theory]
    [InlineData(4825, 600, true)]   // InferDuration(4825) ≈ 600, |600 - 600| = 0 <= 5
    [InlineData(4825, 604, true)]   // |600 - 604| = 4 <= 5
    [InlineData(4825, 605, true)]   // |600 - 605| = 5 <= 5
    [InlineData(4825, 606, false)]  // |600 - 606| = 6 > 5
    [InlineData(4825, 900, false)]  // |600 - 900| = 300 > 5
    public void InferDuration_CrosscheckWithTolerance(int lineCount, double expectedDuration, bool shouldAccept)
    {
        var inferred = ChromaprintConstants.InferDuration(lineCount);
        var withinTolerance = Math.Abs(inferred - expectedDuration) <= ChromaprintConstants.DurationTolerance;
        Assert.Equal(shouldAccept, withinTolerance);
    }
}
