// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using Xunit;

namespace IntroSkipper.Tests;

public class TestPluginConfiguration
{
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
