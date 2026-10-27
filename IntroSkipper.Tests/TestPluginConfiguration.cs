// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Xml.Serialization;
using IntroSkipper.Configuration;
using Xunit;

namespace IntroSkipper.Tests;

public class TestPluginConfiguration
{
    [Fact]
    public void XmlSerialization_RoundTripsStructuredExclusionListsWithCommaValues()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration
        {
            SeriesExclusions = { "Show, With Comma", "The Office" },
            MovieExclusions = { "Movie, Part 1" },
            PathExclusions = { @"C:\Media, Remote" }
        };

        using var writer = new StringWriter();
        serializer.Serialize(writer, config);

        var xml = writer.ToString();
        Assert.Contains("<string>Show, With Comma</string>", xml, StringComparison.Ordinal);
        Assert.Contains("<string>Movie, Part 1</string>", xml, StringComparison.Ordinal);
        Assert.Contains(@"<string>C:\Media, Remote</string>", xml, StringComparison.Ordinal);

        using var reader = new StringReader(xml);
        var roundTripped = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.Equal(config.SeriesExclusions, roundTripped.SeriesExclusions);
        Assert.Equal(config.MovieExclusions, roundTripped.MovieExclusions);
        Assert.Equal(config.PathExclusions, roundTripped.PathExclusions);
    }

    [Fact]
    public void JsonDeserialization_PopulatesStructuredExclusionLists()
    {
        var config = JsonSerializer.Deserialize<PluginConfiguration>(
            """
            {
              "SeriesExclusions": ["The Office", "Show, With Comma"],
              "MovieExclusions": ["The Matrix"],
              "PathExclusions": ["/mnt/remote"]
            }
            """);

        Assert.NotNull(config);
        Assert.Equal(["The Office", "Show, With Comma"], config.SeriesExclusions);
        Assert.Equal(["The Matrix"], config.MovieExclusions);
        Assert.Equal(["/mnt/remote"], config.PathExclusions);
    }

    [Fact]
    public void JsonSerialization_WritesStructuredExclusionListsAsArrays()
    {
        var config = new PluginConfiguration
        {
            SeriesExclusions = { "The Office" },
            MovieExclusions = { "The Matrix" },
            PathExclusions = { "/mnt/remote" }
        };

        var json = JsonSerializer.Serialize(config);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.GetProperty("SeriesExclusions").ValueKind);
        Assert.Equal("The Office", root.GetProperty("SeriesExclusions")[0].GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("MovieExclusions").ValueKind);
        Assert.Equal("The Matrix", root.GetProperty("MovieExclusions")[0].GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("PathExclusions").ValueKind);
        Assert.Equal("/mnt/remote", root.GetProperty("PathExclusions")[0].GetString());
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

    [Theory]
    [InlineData(PluginConfiguration.LegacyDefaultRecapPattern, true)]
    [InlineData(PluginConfiguration.LegacyDefaultRecapPatternBefore101122, true)]
    [InlineData(PluginConfiguration.LegacyDefaultRecapPattern + "|Rückblick", false)]
    [InlineData("Recap", false)]
    public void UpgradeLegacyDefaults_ReplacesOnlyAnUntouchedOldRecapDefault(string saved, bool upgraded)
    {
        var config = new PluginConfiguration { ChapterAnalyzerRecapPattern = saved };

        config.UpgradeLegacyDefaults();

        Assert.Equal(upgraded ? new PluginConfiguration().ChapterAnalyzerRecapPattern : saved, config.ChapterAnalyzerRecapPattern);
    }

    [Theory]
    [InlineData(PluginConfiguration.LegacyDefaultIntroPattern, true)]
    [InlineData(PluginConfiguration.LegacyDefaultIntroPatternBefore101122, true)]
    [InlineData(PluginConfiguration.LegacyDefaultIntroPatternBefore101010, true)]
    [InlineData(PluginConfiguration.LegacyDefaultIntroPattern + "|Vorspann", false)]
    public void UpgradeLegacyDefaults_ReplacesOnlyAnUntouchedOldIntroDefault(string saved, bool upgraded)
    {
        var config = new PluginConfiguration { ChapterAnalyzerIntroductionPattern = saved };

        config.UpgradeLegacyDefaults();

        Assert.Equal(upgraded ? new PluginConfiguration().ChapterAnalyzerIntroductionPattern : saved, config.ChapterAnalyzerIntroductionPattern);
    }

    [Theory]
    [InlineData(PluginConfiguration.LegacyDefaultCreditsPattern, true)]
    [InlineData(PluginConfiguration.LegacyDefaultCreditsPatternBefore101122, true)]
    [InlineData(PluginConfiguration.LegacyDefaultCreditsPatternBefore101010, true)]
    [InlineData(PluginConfiguration.LegacyDefaultCreditsPatternWithoutOutro, true)]
    [InlineData(PluginConfiguration.LegacyDefaultCreditsPatternWithoutEd, true)]
    [InlineData(PluginConfiguration.LegacyDefaultCreditsPattern + "|Abspann", false)]
    public void UpgradeLegacyDefaults_ReplacesOnlyAnUntouchedOldCreditsDefault(string saved, bool upgraded)
    {
        var config = new PluginConfiguration { ChapterAnalyzerEndCreditsPattern = saved };

        config.UpgradeLegacyDefaults();

        Assert.Equal(upgraded ? new PluginConfiguration().ChapterAnalyzerEndCreditsPattern : saved, config.ChapterAnalyzerEndCreditsPattern);
    }
}
