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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class TestChapterAnalyzer
{
    [Theory]
    [InlineData("Opening")]
    [InlineData("OP")]
    [InlineData("Intro")]
    [InlineData("Intro:")]
    [InlineData("Intro Start")]
    [InlineData("Introduction")]
    [InlineData("オープニング")]
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
    [InlineData("Credits:")]
    [InlineData("エンディング")]
    [InlineData("Générique")]
    [InlineData("Abspann")]
    public void TestEndCreditsExpression(string chapterName)
    {
        var creditsChapter = FindChapter(chapterName, AnalysisMode.Credits);

        Assert.NotNull(creditsChapter);
        Assert.Equal(1890, creditsChapter.Start);
        Assert.Equal(2000, creditsChapter.End);
    }

    [Theory]
    [InlineData("Preview")]
    [InlineData("Trailer")]
    [InlineData("予告")]
    public void TestPreviewExpression(string chapterName)
    {
        var previewChapter = FindChapter(chapterName, AnalysisMode.Preview);

        Assert.NotNull(previewChapter);
        Assert.Equal(1890, previewChapter.Start);
        Assert.Equal(2000, previewChapter.End);
    }

    [Theory]
    [InlineData("Recap")]
    [InlineData("Previously")]
    [InlineData("前回のあらすじ")]
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
    [InlineData("Intermission")]
    public void TestCommercialExpression(string chapterName)
    {
        var commercialChapter = FindChapter(chapterName, AnalysisMode.Commercial);

        Assert.NotNull(commercialChapter);
        Assert.Equal(60, commercialChapter.Start);
        Assert.Equal(90, commercialChapter.End);
    }

    [Theory]
    [InlineData("Intermission/Intro")]
    [InlineData("Intermission/Intro Animation")]
    [InlineData("Commercial End")]
    [InlineData("Intermission End")]
    [InlineData("Intermission/Intro End")]
    [InlineData("Intermission/Intro Animation End")]
    public void TestCommercialExpressionIgnoresSponsorBlockOnlyAndEndLabels(string chapterName)
    {
        var commercialChapter = FindChapter(chapterName, AnalysisMode.Commercial);

        Assert.Null(commercialChapter);
    }

    [Theory]
    [InlineData("Intro: End", AnalysisMode.Introduction)]
    [InlineData("Credits: End", AnalysisMode.Credits)]
    [InlineData("Preview: End", AnalysisMode.Preview)]
    [InlineData("Recap: End", AnalysisMode.Recap)]
    [InlineData("Commercial: End", AnalysisMode.Commercial)]
    [InlineData("Intermission: End", AnalysisMode.Commercial)]
    public void TestChapterExpressionIgnoresColonDelimitedEndLabels(string chapterName, AnalysisMode mode)
    {
        var chapter = FindChapter(chapterName, mode);

        Assert.Null(chapter);
    }

    [Theory]
    [InlineData("[SponsorBlock]: Intro", AnalysisMode.Introduction)]
    [InlineData("[SponsorBlock]: intro", AnalysisMode.Introduction)]
    [InlineData("[SponsorBlock]: Endcards/Credits", AnalysisMode.Credits)]
    [InlineData("[SponsorBlock]: Outro", AnalysisMode.Credits)]
    [InlineData("[SponsorBlock]: outro", AnalysisMode.Credits)]
    [InlineData("[SponsorBlock]: Preview", AnalysisMode.Preview)]
    [InlineData("[SponsorBlock]: preview", AnalysisMode.Preview)]
    [InlineData("[SponsorBlock]: Recap", AnalysisMode.Recap)]
    [InlineData("[SponsorBlock]: Sponsor", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: sponsor", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Unpaid/Self Promotion", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Self Promotion", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: selfpromo", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Interaction Reminder (Subscribe)", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: interaction", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Tangents/Jokes", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Filler", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: filler", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Music: Non-Music Section", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Non-Music Section", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: music_offtopic", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Intermission", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Intermission/Intro Animation", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Preview/Recap", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Preview/Recap/Hook", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Hook/Greetings", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: hook", AnalysisMode.Commercial)]
    public void TestSponsorBlockChapterLabelsMapToExpectedMode(string chapterName, AnalysisMode expectedMode)
    {
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            var chapter = FindChapter(chapterName, mode, expressionOverride: string.Empty);

            if (mode == expectedMode)
            {
                Assert.NotNull(chapter);
            }
            else
            {
                Assert.Null(chapter);
            }
        }
    }

    [Fact]
    public void TestSponsorBlockDetectionCanBeDisabled()
    {
        var commercialChapter = FindChapter(
            "[SponsorBlock]: Intermission/Intro Animation",
            AnalysisMode.Commercial,
            expressionOverride: string.Empty,
            enableSponsorBlockChapterDetection: false);

        Assert.Null(commercialChapter);
    }

    [Fact]
    public void TestUnmappedSponsorBlockChapterFallsBackToUserRegex()
    {
        var introChapter = FindChapter(
            "[SponsorBlock]: Custom Intro",
            AnalysisMode.Introduction,
            expressionOverride: "Custom Intro");

        Assert.NotNull(introChapter);
        Assert.Equal(60, introChapter.Start);
        Assert.Equal(90, introChapter.End);
    }

    private Segment? FindChapter(
        string chapterName,
        AnalysisMode mode,
        string? expressionOverride = null,
        bool enableSponsorBlockChapterDetection = true)
    {
        var analyzer = new ChapterAnalyzer(NullLogger<ChapterAnalyzer>.Instance, null!);
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
