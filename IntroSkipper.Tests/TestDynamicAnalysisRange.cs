// SPDX-FileCopyrightText: 2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Manager;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// Tests for the duration-scaled Chromaprint analysis range introduced in
/// <see cref="QueueManager.ComputeDynamicFingerprintDuration"/>.
/// </summary>
public class TestDynamicAnalysisRange
{
    // Short episodes (< 15 min / 900 s): 50 % up to 360 s

    [Theory]
    [InlineData(600, 300)]
    [InlineData(660, 330)]
    [InlineData(899, 360)]
    public void ShortEpisode_Returns50PercentUpTo360Seconds(double duration, double expected)
    {
        var result = QueueManager.ComputeDynamicFingerprintDuration(duration, PluginConfiguration.DefaultAnalysisLengthLimit);

        Assert.Equal(expected, result, 1);
    }

    // Medium episodes (15-30 min): 35 % up to 480 s

    [Theory]
    [InlineData(900, 315)]
    [InlineData(1200, 420)]
    [InlineData(1799, 480)]
    public void MediumEpisode_Returns35PercentUpTo480Seconds(double duration, double expected)
    {
        var result = QueueManager.ComputeDynamicFingerprintDuration(duration, PluginConfiguration.DefaultAnalysisLengthLimit);

        Assert.Equal(expected, result, 1);
    }

    // Long episodes (>= 30 min): 15 % up to 600 s

    [Theory]
    [InlineData(1800, 270)]
    [InlineData(2700, 405)]
    [InlineData(4800, 600)]
    public void LongEpisode_Returns15PercentUpTo600Seconds(double duration, double expected)
    {
        var result = QueueManager.ComputeDynamicFingerprintDuration(duration, PluginConfiguration.DefaultAnalysisLengthLimit);

        Assert.Equal(expected, result, 1);
    }

    // AnalysisLengthLimit acts as an absolute cap

    [Fact]
    public void AnalysisLengthLimit_CapsResultBelowTierMaximum()
    {
        // 45-min episode with tier cap 600 s, but admin lowered limit to 5 min (300 s)
        const double duration = 2700;
        const int limitMinutes = 5;

        var result = QueueManager.ComputeDynamicFingerprintDuration(duration, limitMinutes);

        Assert.Equal(300, result, 1);
    }

    // UseDynamicAnalysisRange flag: enabled by default

    [Fact]
    public void UseDynamicAnalysisRange_IsTrueByDefault()
    {
        var config = new PluginConfiguration();

        Assert.True(config.UseDynamicAnalysisRange);
    }
}
