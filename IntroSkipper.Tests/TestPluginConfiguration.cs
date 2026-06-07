// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using Xunit;

namespace IntroSkipper.Tests;

public class TestPluginConfiguration
{
    [Fact]
    public void Constructor_UsesConfiguredAnalysisDefaults()
    {
        var config = new PluginConfiguration();

        Assert.Equal(PluginConfiguration.DefaultAnalysisPercent, config.AnalysisPercent);
        Assert.Equal(PluginConfiguration.DefaultAnalysisLengthLimit, config.AnalysisLengthLimit);
        Assert.Equal(PluginConfiguration.DefaultMinimumIntroDuration, config.MinimumIntroDuration);
        Assert.Equal(15, config.MinimumRecapDetectionDuration);
        Assert.Equal(120, config.MaximumRecapDetectionDuration);
        Assert.False(config.DetectRecapUsingFirstBlackFrame);
    }

    [Theory]
    [InlineData(PluginConfiguration.MinimumAnalysisPercent - 2, PluginConfiguration.MinimumAnalysisPercent)]
    [InlineData(PluginConfiguration.MinimumAnalysisPercent - 1, PluginConfiguration.MinimumAnalysisPercent)]
    [InlineData(PluginConfiguration.DefaultAnalysisPercent, PluginConfiguration.DefaultAnalysisPercent)]
    [InlineData(PluginConfiguration.MaximumAnalysisPercent, PluginConfiguration.MaximumAnalysisPercent)]
    [InlineData(PluginConfiguration.MaximumAnalysisPercent + 1, PluginConfiguration.MaximumAnalysisPercent)]
    public void AnalysisPercent_Setter_ClampsValuesWithinConfiguredBounds(int value, int expected)
    {
        var config = new PluginConfiguration { AnalysisPercent = value };

        Assert.Equal(expected, config.AnalysisPercent);
    }
}
