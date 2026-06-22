// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
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
    public void IsPathExcluded_MatchesExactPathOrChildrenOnly(string path, string fragment, bool expected)
    {
        var config = new PluginConfiguration
        {
            PathExclusions = { fragment }
        };

        var policy = ExclusionPolicy.FromConfiguration(config);

        Assert.Equal(expected, policy.IsPathExcluded(path));
    }

    [Fact]
    public void FromConfiguration_RejectsEmptyPathFragments()
    {
        var config = new PluginConfiguration
        {
            PathExclusions = { string.Empty, "   " }
        };

        var policy = ExclusionPolicy.FromConfiguration(config);

        Assert.False(policy.IsPathExcluded("/mnt/rd/Show/S01E01.mkv"));
    }

    [Fact]
    public void EvaluateSeries_UsesStructuredExclusions()
    {
        var config = new PluginConfiguration
        {
            SeriesExclusions = { "Structured Show" }
        };

        var policy = ExclusionPolicy.FromConfiguration(config);

        Assert.True(policy.EvaluateSeries("structured show", Guid.NewGuid(), "/media/show.mkv").IsExcluded);
        Assert.False(policy.EvaluateSeries("Other Show", Guid.NewGuid(), "/media/other.mkv").IsExcluded);
    }

    [Fact]
    public void EvaluateMovie_UsesMovieExclusions()
    {
        var config = new PluginConfiguration
        {
            MovieExclusions = { "Excluded Movie" }
        };

        var policy = ExclusionPolicy.FromConfiguration(config);

        Assert.True(policy.EvaluateMovie("excluded movie", Guid.NewGuid(), "/media/excluded.mkv").IsExcluded);
        Assert.False(policy.EvaluateMovie("Other Movie", Guid.NewGuid(), "/media/other.mkv").IsExcluded);
    }

    [Fact]
    public void EvaluateSeries_ExcludesByPathBeforeName()
    {
        var config = new PluginConfiguration
        {
            PathExclusions = { "/mnt/rd" }
        };

        var policy = ExclusionPolicy.FromConfiguration(config);
        var decision = policy.EvaluateSeries("Any Show", Guid.NewGuid(), "/mnt/rd/Any Show/S01E01.mkv");

        Assert.True(decision.IsExcluded);
        Assert.Equal(ExclusionReason.Path, decision.Reason);
        Assert.Equal("PathExclusions", decision.RuleLabel);
    }
}
