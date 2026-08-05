// SPDX-FileCopyrightText: 2026 rlauuzo
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
    public void Constructor_UsesConfiguredAnalysisDefaults()
    {
        var config = new PluginConfiguration();

        Assert.Equal(PluginConfiguration.DefaultAnalysisPercent, config.AnalysisPercent);
        Assert.Equal(PluginConfiguration.DefaultAnalysisLengthLimit, config.AnalysisLengthLimit);
        Assert.Equal(PluginConfiguration.DefaultMinimumIntroDuration, config.MinimumIntroDuration);
        Assert.Equal(15, config.MinimumRecapDetectionDuration);
        Assert.Equal(120, config.MaximumRecapDetectionDuration);
        Assert.False(config.DetectRecapUsingBlackFrames);
        Assert.Equal(PluginConfiguration.DefaultSettledSeasonDelayHours, config.SettledSeasonDelayHours);
        Assert.Equal(string.Empty, config.ExcludeSeries);
        Assert.Equal(string.Empty, config.PreferredAudioLanguage);
        Assert.True(config.PreferAudioStreamWithMostChannels);
        Assert.Empty(config.SeriesExclusions);
        Assert.Empty(config.MovieExclusions);
        Assert.Empty(config.PathExclusions);
    }

    [Fact]
    public void ExclusionLists_AreMutableCollections()
    {
        var config = new PluginConfiguration();

        config.SeriesExclusions.Add("The Office");
        config.MovieExclusions.Add("The Matrix");
        config.PathExclusions.Add("/mnt/remote");

        Assert.Equal(["The Office"], config.SeriesExclusions);
        Assert.Equal(["The Matrix"], config.MovieExclusions);
        Assert.Equal(["/mnt/remote"], config.PathExclusions);
    }

    [Fact]
    public void XmlSerialization_RoundTripsStructuredExclusionListsWithCommaValues()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration
        {
            ExcludeSeries = "Legacy Show",
            SeriesExclusions = { "Show, With Comma", "The Office" },
            MovieExclusions = { "Movie, Part 1" },
            PathExclusions = { @"C:\Media, Remote" }
        };

        using var writer = new StringWriter();
        serializer.Serialize(writer, config);

        var xml = writer.ToString();
        Assert.Contains("<ExcludeSeries>Legacy Show</ExcludeSeries>", xml, StringComparison.Ordinal);
        Assert.Contains("<string>Show, With Comma</string>", xml, StringComparison.Ordinal);
        Assert.Contains("<string>Movie, Part 1</string>", xml, StringComparison.Ordinal);
        Assert.Contains(@"<string>C:\Media, Remote</string>", xml, StringComparison.Ordinal);

        using var reader = new StringReader(xml);
        var roundTripped = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.Equal(config.ExcludeSeries, roundTripped.ExcludeSeries);
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
              "ExcludeSeries": "Legacy Show",
              "SeriesExclusions": ["The Office", "Show, With Comma", null],
              "MovieExclusions": ["The Matrix"],
              "PathExclusions": ["/mnt/remote"]
            }
            """);

        Assert.NotNull(config);
        Assert.Equal("Legacy Show", config.ExcludeSeries);
        Assert.Equal(["The Office", "Show, With Comma"], config.SeriesExclusions);
        Assert.Equal(["The Matrix"], config.MovieExclusions);
        Assert.Equal(["/mnt/remote"], config.PathExclusions);
    }

    [Fact]
    public void JsonSerialization_WritesStructuredExclusionListsAsArrays()
    {
        var config = new PluginConfiguration
        {
            ExcludeSeries = "Legacy Show",
            SeriesExclusions = { "The Office" },
            MovieExclusions = { "The Matrix" },
            PathExclusions = { "/mnt/remote" }
        };

        var json = JsonSerializer.Serialize(config);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("Legacy Show", root.GetProperty("ExcludeSeries").GetString());
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
}
