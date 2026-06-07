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
    public void TestIntroductionExpression(string chapterName)
    {
        var chapters = CreateChapters(chapterName, AnalysisMode.Introduction);
        var introChapter = FindChapter(chapters, AnalysisMode.Introduction);

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
    public void TestEndCreditsExpression(string chapterName)
    {
        var chapters = CreateChapters(chapterName, AnalysisMode.Credits);
        var creditsChapter = FindChapter(chapters, AnalysisMode.Credits);

        Assert.NotNull(creditsChapter);
        Assert.Equal(1890, creditsChapter.Start);
        Assert.Equal(2000, creditsChapter.End);
    }

    [Fact]
    public void BuildRecapFromBlackFrames_ReturnsSegmentFromStartToLatestFrameInRange()
    {
        var episodeId = Guid.NewGuid();
        var frames = new List<BlackFrame>
        {
            new(95, 32.5, 123),
            new(92, 18.25, 90),
            new(90, 45, 150),
        };

        var recap = ChapterAnalyzer.BuildRecapFromBlackFrames(episodeId, frames, minimumRecapDuration: 5, maximumRecapBoundary: 120);

        Assert.NotNull(recap);
        Assert.Equal(episodeId, recap.EpisodeId);
        Assert.Equal(0, recap.Start);
        Assert.Equal(45, recap.End);
    }

    [Fact]
    public void BuildRecapFromBlackFrames_ReturnsLatestFrameBeforeIntroBoundary()
    {
        var episodeId = Guid.NewGuid();
        var frames = new List<BlackFrame>
        {
            new(95, 32.5, 123),
            new(92, 72, 250),
            new(90, 90, 300),
        };

        var recap = ChapterAnalyzer.BuildRecapFromBlackFrames(episodeId, frames, minimumRecapDuration: 5, maximumRecapBoundary: 80);

        Assert.NotNull(recap);
        Assert.Equal(72, recap.End);
    }

    [Fact]
    public void BuildRecapFromBlackFrames_ReturnsNull_WhenFrameBeforeMinimumDuration()
    {
        var frames = new List<BlackFrame> { new(90, 3.5, 20) };

        var recap = ChapterAnalyzer.BuildRecapFromBlackFrames(Guid.NewGuid(), frames, minimumRecapDuration: 5, maximumRecapBoundary: 120);

        Assert.Null(recap);
    }

    private Segment? FindChapter(Collection<ChapterInfo> chapters, AnalysisMode mode)
    {
        var logger = new LoggerFactory().CreateLogger<ChapterAnalyzer>();
        var analyzer = new ChapterAnalyzer(logger);

        var config = new Configuration.PluginConfiguration();
        var expression = mode == AnalysisMode.Introduction ?
            config.ChapterAnalyzerIntroductionPattern :
            config.ChapterAnalyzerEndCreditsPattern;

        return analyzer.FindMatchingChapter(new() { Duration = 2000 }, chapters, expression, mode);
    }

    private Collection<ChapterInfo> CreateChapters(string name, AnalysisMode mode)
    {
        var chapters = new[]{
            CreateChapter("Cold Open", 0),
            CreateChapter(mode == AnalysisMode.Introduction ? name : "Introduction", 60),
            CreateChapter("Main Episode", 90),
            CreateChapter(mode == AnalysisMode.Credits ? name : "Credits", 1890)
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
