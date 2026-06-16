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
        Assert.Equal(PluginConfiguration.DefaultSettledSeasonRescanPeriodDays, config.SettledSeasonRescanPeriodDays);
        Assert.Equal(PluginConfiguration.DefaultSettledSeasonDelayHours, config.SettledSeasonDelayHours);
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

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(PluginConfiguration.MaximumSettledSeasonRescanPeriodDays, PluginConfiguration.MaximumSettledSeasonRescanPeriodDays)]
    [InlineData(PluginConfiguration.MaximumSettledSeasonRescanPeriodDays + 1, PluginConfiguration.MaximumSettledSeasonRescanPeriodDays)]
    [InlineData(int.MaxValue, PluginConfiguration.MaximumSettledSeasonRescanPeriodDays)]
    public void SettledSeasonRescanPeriodDays_Setter_ClampsValuesWithinConfiguredBounds(int value, int expected)
    {
        var config = new PluginConfiguration { SettledSeasonRescanPeriodDays = value };

        Assert.Equal(expected, config.SettledSeasonRescanPeriodDays);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(24, 24)]
    [InlineData(48, 48)]
    [InlineData(PluginConfiguration.MaximumSettledSeasonDelayHours, PluginConfiguration.MaximumSettledSeasonDelayHours)]
    [InlineData(PluginConfiguration.MaximumSettledSeasonDelayHours + 1, PluginConfiguration.MaximumSettledSeasonDelayHours)]
    [InlineData(int.MaxValue, PluginConfiguration.MaximumSettledSeasonDelayHours)]
    public void SettledSeasonDelayHours_Setter_ClampsValuesWithinConfiguredBounds(int value, int expected)
    {
        var config = new PluginConfiguration { SettledSeasonDelayHours = value };

        Assert.Equal(expected, config.SettledSeasonDelayHours);
    }
}
