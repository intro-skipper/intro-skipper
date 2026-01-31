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

    [Fact]
    public void TestMultipleMatchingChapters()
    {
        // Create chapters with multiple intros (e.g., recap + opening)
        var chapters = new Collection<ChapterInfo>(new List<ChapterInfo>
        {
            CreateChapter("Cold Open", 0),
            CreateChapter("Opening", 60),       // First matching intro
            CreateChapter("Main Episode", 120),
            CreateChapter("Opening Credits", 500),  // Second matching intro
            CreateChapter("Episode Part 2", 560),
            CreateChapter("Credits", 1890)
        });

        var logger = new LoggerFactory().CreateLogger<ChapterAnalyzer>();
        var analyzer = new ChapterAnalyzer(logger);
        var config = new Configuration.PluginConfiguration();
        config.FullLengthChapters = true; // Allow any duration

        // Note: The analyzer uses Plugin.Instance?.Configuration, so we test the method directly
        var segments = analyzer.FindMatchingChapters(
            new() { Duration = 2000 },
            chapters,
            config.ChapterAnalyzerIntroductionPattern,
            AnalysisMode.Introduction);

        // Should find both "Opening" and "Opening Credits"
        Assert.Equal(2, segments.Count);
        Assert.Equal(60, segments[0].Start);
        Assert.Equal(120, segments[0].End);
        Assert.Equal(500, segments[1].Start);
        Assert.Equal(560, segments[1].End);
    }

    private Segment? FindChapter(Collection<ChapterInfo> chapters, AnalysisMode mode)
    {
        var logger = new LoggerFactory().CreateLogger<ChapterAnalyzer>();
        var analyzer = new ChapterAnalyzer(logger);

        var config = new Configuration.PluginConfiguration();
        var expression = mode == AnalysisMode.Introduction ?
            config.ChapterAnalyzerIntroductionPattern :
            config.ChapterAnalyzerEndCreditsPattern;

        var segments = analyzer.FindMatchingChapters(new() { Duration = 2000 }, chapters, expression, mode);
        return segments.Count > 0 ? segments[0] : null;
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
