// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Helper;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestExclusionPolicy
{
    [Theory]
    [InlineData("/mnt/rd/Series/Season 01/S01E01.mkv", "/mnt/rd", true)]
    [InlineData("/mnt/rd", "/mnt/rd/", true)]
    [InlineData("/mnt/rd-backup/Series/S01E01.mkv", "/mnt/rd", false)]
    [InlineData("/mnt/RD/Series/S01E01.mkv", "/mnt/rd", true)]
    [InlineData(@"C:\Media\Real-Debrid\Show\S01E01.mkv", @"C:\Media\Real-Debrid", true)]
    [InlineData(@"C:\Media\Real-Debrid-Backup\Show\S01E01.mkv", @"C:\Media\Real-Debrid", false)]
    [InlineData(@"\\server\share\Show\S01E01.mkv", @"\\server\share", true)]
    [InlineData("/media/zurg/Movies/Film.mkv", "zurg", false)]
    public void PathExclusion_MatchesExactPathOrChildrenOnly(string path, string fragment, bool expected)
    {
        var config = new PluginConfiguration
        {
            PathExclusions = { fragment }
        };

        var policy = ExclusionPolicy.FromConfiguration(config);

        Assert.Equal(expected, policy.EvaluateSeries(null, path).IsExcluded);
    }

    [Fact]
    public void FromConfiguration_RejectsEmptyPathFragments()
    {
        var config = new PluginConfiguration
        {
            PathExclusions = { string.Empty, "   " }
        };

        var policy = ExclusionPolicy.FromConfiguration(config);

        Assert.False(policy.EvaluateSeries(null, "/mnt/rd/Show/S01E01.mkv").IsExcluded);
    }

    [Fact]
    public void EvaluateSeries_UsesStructuredExclusions()
    {
        var config = new PluginConfiguration
        {
            SeriesExclusions = { "Structured Show" }
        };

        var policy = ExclusionPolicy.FromConfiguration(config);

        Assert.True(policy.EvaluateSeries("structured show", "/media/show.mkv").IsExcluded);
        Assert.False(policy.EvaluateSeries("Other Show", "/media/other.mkv").IsExcluded);
    }

    [Fact]
    public void EvaluateMovie_UsesMovieExclusions()
    {
        var config = new PluginConfiguration
        {
            MovieExclusions = { "Excluded Movie" }
        };

        var policy = ExclusionPolicy.FromConfiguration(config);

        Assert.True(policy.EvaluateMovie("excluded movie", "/media/excluded.mkv").IsExcluded);
        Assert.False(policy.EvaluateMovie("Other Movie", "/media/other.mkv").IsExcluded);
    }

    [Fact]
    public void EvaluateSeries_ExcludesByPathBeforeName()
    {
        var config = new PluginConfiguration
        {
            PathExclusions = { "/mnt/rd" }
        };

        var policy = ExclusionPolicy.FromConfiguration(config);
        var decision = policy.EvaluateSeries("Any Show", "/mnt/rd/Any Show/S01E01.mkv");

        Assert.True(decision.IsExcluded);
        Assert.Equal("PathExclusions", decision.RuleLabel);
    }
}
