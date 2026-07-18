// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestChromaprintFinalSampleMatch
{
    [Fact]
    public void CompareEpisodes_ReturnsMatchEndingAtFinalSample()
    {
        using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = Plugin.Instance;
        Assert.NotNull(plugin);
        EntrypointTestHelpers.SetPropertyOrField(
            plugin,
            "Configuration",
            new PluginConfiguration
            {
                MinimumIntroDuration = 1,
                MaximumFingerprintPointDifferences = 0,
                MaximumTimeSkip = 0.2,
                InvertedIndexShift = 0,
            });
        uint[] fingerprint = [
            0x1000u, 0x1100u, 0x1200u, 0x1300u, 0x1400u,
            0x1500u, 0x1600u, 0x1700u, 0x1800u, 0x1900u,
        ];
        var lhsId = Guid.NewGuid();
        var rhsId = Guid.NewGuid();
        var analyzer = new ChromaprintAnalyzer(
            NullLogger<ChromaprintAnalyzer>.Instance,
            null!,
            null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase());

        var (lhs, rhs) = analyzer.CompareEpisodes(lhsId, fingerprint, rhsId, fingerprint);

        var expectedEnd = (fingerprint.Length - 1) * ChromaprintConstants.SampleDuration;
        Assert.True(lhs.Valid);
        Assert.True(rhs.Valid);
        Assert.Equal(lhsId, lhs.EpisodeId);
        Assert.Equal(rhsId, rhs.EpisodeId);
        Assert.Equal(expectedEnd, lhs.End);
        Assert.Equal(expectedEnd, rhs.End);
    }
}
