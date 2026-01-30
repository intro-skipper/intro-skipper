// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
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

    [Fact]
    public void TestMultipleCommercialSegments()
    {
        var logger = new LoggerFactory().CreateLogger<ChapterAnalyzer>();
        var analyzer = new ChapterAnalyzer(logger);
        var config = new Configuration.PluginConfiguration();

        // Create chapters with multiple commercial breaks
        var chapters = new Collection<ChapterInfo>(new List<ChapterInfo>
        {
            CreateChapter("Cold Open", 0),
            CreateChapter("Commercial Break 1", 300), // 5:00 - 7:00 (2 min)
            CreateChapter("Act 1", 420),
            CreateChapter("Commercial Break 2", 900), // 15:00 - 16:00 (1 min)
            CreateChapter("Act 2", 960),
            CreateChapter("Commercial Break 3", 1500), // 25:00 - 26:30 (1.5 min)
            CreateChapter("Act 3", 1590),
            CreateChapter("Credits", 1800)
        });

        var episode = new QueuedEpisode { Duration = 2000 };
        var commercials = analyzer.FindAllMatchingChapters(
            episode,
            chapters,
            config.ChapterAnalyzerCommercialPattern,
            AnalysisMode.Commercial);

        // Should find all 3 commercial breaks
        Assert.Equal(3, commercials.Count);

        // Verify first commercial
        Assert.Equal(300, commercials[0].Start);
        Assert.Equal(420, commercials[0].End);

        // Verify second commercial
        Assert.Equal(900, commercials[1].Start);
        Assert.Equal(960, commercials[1].End);

        // Verify third commercial
        Assert.Equal(1500, commercials[2].Start);
        Assert.Equal(1590, commercials[2].End);
    }

    [Fact]
    public void TestSingleCommercialSegment()
    {
        var logger = new LoggerFactory().CreateLogger<ChapterAnalyzer>();
        var analyzer = new ChapterAnalyzer(logger);
        var config = new Configuration.PluginConfiguration();

        // Create chapters with a single commercial break
        var chapters = new Collection<ChapterInfo>(new List<ChapterInfo>
        {
            CreateChapter("Cold Open", 0),
            CreateChapter("Commercial Break", 300),
            CreateChapter("Main Episode", 420),
            CreateChapter("Credits", 1800)
        });

        var episode = new QueuedEpisode { Duration = 2000 };
        var commercials = analyzer.FindAllMatchingChapters(
            episode,
            chapters,
            config.ChapterAnalyzerCommercialPattern,
            AnalysisMode.Commercial);

        // Should find 1 commercial break
        Assert.Single(commercials);
        Assert.Equal(300, commercials[0].Start);
        Assert.Equal(420, commercials[0].End);
    }

    [Fact]
    public void TestNoCommercialSegments()
    {
        var logger = new LoggerFactory().CreateLogger<ChapterAnalyzer>();
        var analyzer = new ChapterAnalyzer(logger);
        var config = new Configuration.PluginConfiguration();

        // Create chapters without any commercial breaks
        var chapters = new Collection<ChapterInfo>(new List<ChapterInfo>
        {
            CreateChapter("Cold Open", 0),
            CreateChapter("Main Episode", 300),
            CreateChapter("Credits", 1800)
        });

        var episode = new QueuedEpisode { Duration = 2000 };
        var commercials = analyzer.FindAllMatchingChapters(
            episode,
            chapters,
            config.ChapterAnalyzerCommercialPattern,
            AnalysisMode.Commercial);

        // Should find no commercial breaks
        Assert.Empty(commercials);
    }
}
