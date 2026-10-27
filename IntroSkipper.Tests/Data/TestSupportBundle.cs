// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestSupportBundle
{
    [Fact]
    public void Markdown_RendersEntriesAsBulletsAndCollapsedTextInDetails()
    {
        var bundle = new SupportBundle(
        [
            new("Overview") { Entries = [new("Plugin version", "1.2.3"), new("FFmpeg", "okay")] },
            new("FFmpeg version", Collapsed: true) { Text = "ffmpeg version 7.1\nbuilt with gcc" },
        ]);

        Assert.Equal(
            "**Overview**\n\n* Plugin version: `1.2.3`\n* FFmpeg: `okay`\n\n<details>\n<summary>FFmpeg version</summary>\n\n```\nffmpeg version 7.1\nbuilt with gcc\n```\n\n</details>\n",
            bundle.Markdown);
    }

    [Fact]
    public void Markdown_EmptyEntriesRenderAsNone()
    {
        var bundle = new SupportBundle([new("Changed settings") { Entries = [] }]);

        Assert.Equal("**Changed settings**\n\nNone\n", bundle.Markdown);
    }

    [Fact]
    public void Markdown_ValuesWithBackticksGetLongerPaddedCodeSpans()
    {
        var bundle = new SupportBundle([new("Overview") { Entries = [new("Pattern", "a``b`"), new("Empty", string.Empty)] }]);

        Assert.Equal("**Overview**\n\n* Pattern: ``` a``b` ```\n* Empty: \n", bundle.Markdown);
    }

    [Theory]
    [InlineData(" foo ", "`  foo  `")]
    [InlineData("foo ", "` foo  `")]
    [InlineData("   ", "`   `")]
    [InlineData("foo", "`foo`")]
    public void Markdown_ValuesWithBoundarySpacesArePaddedSoCommonMarkKeepsThem(string value, string expectedSpan)
    {
        var bundle = new SupportBundle([new("Overview") { Entries = [new("Value", value)] }]);

        Assert.Equal("**Overview**\n\n* Value: " + expectedSpan + "\n", bundle.Markdown);
    }

    [Fact]
    public void Markdown_TextContainingFenceUsesLongerFence()
    {
        var bundle = new SupportBundle([new("Log", Collapsed: true) { Text = "a\n```\nb" }]);

        Assert.Contains("````\na\n```\nb\n````\n", bundle.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_FenceOutgrowsLongestBacktickRunInText()
    {
        var bundle = new SupportBundle([new("Log", Collapsed: true) { Text = "a\n`````\nb" }]);

        Assert.Contains("``````\na\n`````\nb\n``````\n", bundle.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationReport_DefaultConfigurationHasNoChangedSettings()
    {
        var settings = ConfigurationReport.Enumerate(new PluginConfiguration());

        Assert.NotEmpty(settings);
        Assert.All(settings, s => Assert.True(s.IsDefault, s.Name));
    }

    [Fact]
    public void ConfigurationReport_ReportsChangedValuesWithDefaults()
    {
        var config = new PluginConfiguration { UseLegacyBlackFrameAnalyzer = true, MaximumTimeSkip = 4.25 };
        config.SeriesExclusions.Add("Some Show");

        var changed = ConfigurationReport.Enumerate(config).Where(s => !s.IsDefault).ToDictionary(s => s.Name);

        Assert.Equal(3, changed.Count);
        Assert.Equal(("true", "false"), (changed["UseLegacyBlackFrameAnalyzer"].Value, changed["UseLegacyBlackFrameAnalyzer"].Default));
        Assert.Equal(("4.25", "3.5"), (changed["MaximumTimeSkip"].Value, changed["MaximumTimeSkip"].Default));
        Assert.Equal(("1", "0"), (changed["SeriesExclusions"].Value, changed["SeriesExclusions"].Default));
    }

    [Fact]
    public void ConfigurationReport_SkipsRuntimeStateAndFormatsEnumsByName()
    {
        var settings = ConfigurationReport.Enumerate(new PluginConfiguration());

        Assert.DoesNotContain(settings, s => s.Name == nameof(PluginConfiguration.FileTransformationPluginEnabled));
        Assert.Equal("BelowNormal", settings.Single(s => s.Name == nameof(PluginConfiguration.ProcessPriority)).Value);
    }

    [FactSkipFFmpegTests]
    public async Task FingerprintFailuresAndSettingsReport_LeaveOutMediaNamesAndPaths()
    {
        const string Marker = "PrivateShowMarker";
        var config = new PluginConfiguration();
        config.SeriesExclusions.Add(Marker);
        config.MovieExclusions.Add(Marker);
        config.PathExclusions.Add("/media/" + Marker);
        var episodes = Enumerable.Range(1, 2).Select(number => new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Name = Marker,
            Path = $"/does/not/exist/{Marker} - {number}.mkv",
            EpisodeNumber = number,
            IntroFingerprintEnd = 60,
        }).ToList();
        using var db = new TempSegmentDb();
        var analyzer = new ChromaprintAnalyzer(
            NullLogger<ChromaprintAnalyzer>.Instance,
            FfmpegTestHelpers.CreateFFmpegService(),
            DatabaseTestHelpers.CreateTempCacheService(),
            db.Database,
            new PluginConfiguration());
        WarningManager.ResetFingerprintFailures();

        await analyzer.AnalyzeMediaFiles(episodes, AnalysisMode.Introduction, CancellationToken.None);

        var failures = WarningManager.GetFingerprintFailures().Failures;
        WarningManager.ResetFingerprintFailures();
        Assert.Equal(episodes.Count, failures.Count);
        Assert.All(failures, f => Assert.DoesNotContain(Marker, f, StringComparison.Ordinal));
        Assert.All(episodes, episode => Assert.Contains(failures, f => f.Contains(episode.EpisodeId.ToString("N"), StringComparison.Ordinal)));
        Assert.All(ConfigurationReport.Enumerate(config), s => Assert.DoesNotContain(Marker, s.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void WarningManager_KeepsFingerprintFailuresUntilFullScanReset()
    {
        WarningManager.ResetFingerprintFailures();

        WarningManager.RecordFingerprintFailure("0a1b: chromaprint output was malformed");
        WarningManager.RecordFingerprintFailure("2c3d: chromaprint fingerprinting timed out");

        var (failures, dropped) = WarningManager.GetFingerprintFailures();
        Assert.Equal(["0a1b: chromaprint output was malformed", "2c3d: chromaprint fingerprinting timed out"], failures);
        Assert.Equal(0, dropped);
        Assert.Contains(nameof(PluginWarning.InvalidChromaprintFingerprint), WarningManager.GetWarnings(), StringComparison.Ordinal);

        WarningManager.ResetFingerprintFailures();

        Assert.Empty(WarningManager.GetFingerprintFailures().Failures);
        Assert.DoesNotContain(nameof(PluginWarning.InvalidChromaprintFingerprint), WarningManager.GetWarnings(), StringComparison.Ordinal);
    }

    [Fact]
    public void WarningManager_CountsFingerprintFailuresPastTheCap()
    {
        WarningManager.ResetFingerprintFailures();

        for (var i = 0; i < WarningManager.MaxFingerprintFailures + 3; i++)
        {
            WarningManager.RecordFingerprintFailure($"file {i}");
        }

        var (failures, dropped) = WarningManager.GetFingerprintFailures();
        Assert.Equal(WarningManager.MaxFingerprintFailures, failures.Count);
        Assert.Equal(3, dropped);

        WarningManager.ResetFingerprintFailures();
    }
}
