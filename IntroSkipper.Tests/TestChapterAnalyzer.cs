// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using IntroSkipper.Analyzers;
using IntroSkipper.Data;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

public class TestChapterAnalyzer
{
    [Theory]
    [InlineData("Opening")]
    [InlineData("OP")]
    [InlineData("Intro")]
    [InlineData("Intro Start")]
    [InlineData("Introduction")]
    [InlineData("[SponsorBlock]: Intro")]
    [InlineData("[SponsorBlock]: intro")]
    [InlineData("[SponsorBlock]: Intermission/Intro Animation")]
    public void TestIntroductionExpression(string chapterName)
    {
        var introChapter = FindChapter(chapterName, AnalysisMode.Introduction);

        Assert.NotNull(introChapter);
        Assert.Equal(60, introChapter.Start);
        Assert.Equal(90, introChapter.End);
    }

    [Theory]
    [InlineData("End Credits")]
    [InlineData("Ending")]
    [InlineData("Credit start")]
    [InlineData("Closing Credits")]
    [InlineData("Credits")]
    [InlineData("[SponsorBlock]: Endcards/Credits")]
    [InlineData("[SponsorBlock]: Outro")]
    [InlineData("[SponsorBlock]: outro")]
    public void TestEndCreditsExpression(string chapterName)
    {
        var creditsChapter = FindChapter(chapterName, AnalysisMode.Credits);

        Assert.NotNull(creditsChapter);
        Assert.Equal(1890, creditsChapter.Start);
        Assert.Equal(2000, creditsChapter.End);
    }

    [Theory]
    [InlineData("[SponsorBlock]: Preview")]
    [InlineData("[SponsorBlock]: preview")]
    [InlineData("[SponsorBlock]: Preview/Recap")]
    [InlineData("[SponsorBlock]: Preview/Recap/Hook")]
    [InlineData("[SponsorBlock]: Hook/Greetings")]
    [InlineData("[SponsorBlock]: hook")]
    public void TestPreviewExpression(string chapterName)
    {
        var previewChapter = FindChapter(chapterName, AnalysisMode.Preview);

        Assert.NotNull(previewChapter);
        Assert.Equal(1890, previewChapter.Start);
        Assert.Equal(2000, previewChapter.End);
    }

    [Theory]
    [InlineData("[SponsorBlock]: Recap")]
    [InlineData("[SponsorBlock]: Preview/Recap")]
    [InlineData("[SponsorBlock]: Preview/Recap/Hook")]
    [InlineData("[SponsorBlock]: Hook/Greetings")]
    [InlineData("[SponsorBlock]: hook")]
    public void TestRecapExpression(string chapterName)
    {
        var recapChapter = FindChapter(chapterName, AnalysisMode.Recap);

        Assert.NotNull(recapChapter);
        Assert.Equal(60, recapChapter.Start);
        Assert.Equal(90, recapChapter.End);
    }

    [Theory]
    [InlineData("Ad")]
    [InlineData("Advertisement")]
    [InlineData("Commercial")]
    public void TestCommercialExpression(string chapterName)
    {
        var commercialChapter = FindChapter(chapterName, AnalysisMode.Commercial);

        Assert.NotNull(commercialChapter);
        Assert.Equal(60, commercialChapter.Start);
        Assert.Equal(90, commercialChapter.End);
    }

    [Theory]
    [InlineData("[SponsorBlock]: Sponsor")]
    [InlineData("[SponsorBlock]: sponsor")]
    [InlineData("[SponsorBlock]: Unpaid/Self Promotion")]
    [InlineData("[SponsorBlock]: Self Promotion")]
    [InlineData("[SponsorBlock]: selfpromo")]
    [InlineData("[SponsorBlock]: Interaction Reminder (Subscribe)")]
    [InlineData("[SponsorBlock]: interaction")]
    [InlineData("[SponsorBlock]: Tangents/Jokes")]
    [InlineData("[SponsorBlock]: Filler")]
    [InlineData("[SponsorBlock]: filler")]
    [InlineData("[SponsorBlock]: Music: Non-Music Section")]
    [InlineData("[SponsorBlock]: Non-Music Section")]
    [InlineData("[SponsorBlock]: music_offtopic")]
    public void TestSponsorBlockCommercialExpression(string chapterName)
    {
        var commercialChapter = FindChapter(chapterName, AnalysisMode.Commercial);

        Assert.NotNull(commercialChapter);
        Assert.Equal(60, commercialChapter.Start);
        Assert.Equal(90, commercialChapter.End);
    }

    [Theory]
    [InlineData("[SponsorBlock]: Intermission")]
    [InlineData("[SponsorBlock]: Intermission/Intro Animation")]
    public void TestSponsorBlockIntroDesignationsDoNotTranslateToCommercial(string chapterName)
    {
        var commercialChapter = FindChapter(chapterName, AnalysisMode.Commercial);

        Assert.Null(commercialChapter);
    }

    [Fact]
    public void TestSponsorBlockDetectionWorksWithoutUserRegex()
    {
        var introChapter = FindChapter("[SponsorBlock]: Intermission/Intro Animation", AnalysisMode.Introduction, expressionOverride: string.Empty);

        Assert.NotNull(introChapter);
        Assert.Equal(60, introChapter.Start);
        Assert.Equal(90, introChapter.End);
    }

    [Fact]
    public void TestSponsorBlockDetectionCanBeDisabled()
    {
        var introChapter = FindChapter(
            "[SponsorBlock]: Intermission/Intro Animation",
            AnalysisMode.Introduction,
            expressionOverride: string.Empty,
            enableSponsorBlockChapterDetection: false);

        Assert.Null(introChapter);
    }

    private Segment? FindChapter(
        string chapterName,
        AnalysisMode mode,
        string? expressionOverride = null,
        bool enableSponsorBlockChapterDetection = true)
    {
        var logger = new LoggerFactory().CreateLogger<ChapterAnalyzer>();
        var analyzer = new ChapterAnalyzer(logger);
        var chapters = CreateChapters(chapterName, mode);

        var config = new Configuration.PluginConfiguration();
        var expression = mode switch
        {
            AnalysisMode.Introduction => config.ChapterAnalyzerIntroductionPattern,
            AnalysisMode.Credits => config.ChapterAnalyzerEndCreditsPattern,
            AnalysisMode.Preview => config.ChapterAnalyzerPreviewPattern,
            AnalysisMode.Recap => config.ChapterAnalyzerRecapPattern,
            AnalysisMode.Commercial => config.ChapterAnalyzerCommercialPattern,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        return analyzer.FindMatchingChapter(
            new() { Duration = 2000 },
            chapters,
            expressionOverride ?? expression,
            mode,
            enableSponsorBlockChapterDetection);
    }

    private Collection<ChapterInfo> CreateChapters(string name, AnalysisMode mode)
    {
        var earlyName = mode is AnalysisMode.Introduction or AnalysisMode.Recap or AnalysisMode.Commercial ? name : "Introduction";
        var lateName = mode is AnalysisMode.Credits or AnalysisMode.Preview ? name : "Credits";
        var chapters = new[]
        {
            CreateChapter("Cold Open", 0),
            CreateChapter(earlyName, 60),
            CreateChapter("Main Episode", 90),
            CreateChapter(lateName, 1890)
        };

        return new(new List<ChapterInfo>(chapters));
    }

    /// <summary>
    /// Create a ChapterInfo object.
    /// </summary>
    /// <param name="name">Chapter name.</param>
    /// <param name="position">Chapter position (in seconds).</param>
    /// <returns>ChapterInfo.</returns>
    private static ChapterInfo CreateChapter(string name, int position)
    {
        return new()
        {
            Name = name,
            StartPositionTicks = TimeSpan.FromSeconds(position).Ticks
        };
    }
}
