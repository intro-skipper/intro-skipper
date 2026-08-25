// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
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
        Assert.Equal(("[Some Show]", "[]"), (changed["SeriesExclusions"].Value, changed["SeriesExclusions"].Default));
    }

    [Fact]
    public void ConfigurationReport_SkipsRuntimeStateAndFormatsEnumsByName()
    {
        var settings = ConfigurationReport.Enumerate(new PluginConfiguration());

        Assert.DoesNotContain(settings, s => s.Name == nameof(PluginConfiguration.FileTransformationPluginEnabled));
        Assert.Equal("BelowNormal", settings.Single(s => s.Name == nameof(PluginConfiguration.ProcessPriority)).Value);
    }
}
