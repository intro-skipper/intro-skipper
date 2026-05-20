// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// Matrix tests for <see cref="BaseItemAnalyzerTask.CreateAnalyzerPipeline"/>.
/// Verifies analyzer type and order for every mode × category × ffmpegValid × action combination,
/// plus config-driven overrides (PreferChromaprint, UseAlternativeBlackFrameAnalyzer).
/// </summary>
public class TestAnalyzerPipeline
{
    /// <summary>
    /// Creates a <see cref="BaseItemAnalyzerTask"/> wired for pipeline-only testing.
    /// Only <see cref="BaseItemAnalyzerTask.CreateAnalyzerPipeline"/> is exercised;
    /// all other constructor dependencies are unused and passed as null.
    /// </summary>
    private static BaseItemAnalyzerTask CreateTask(PluginConfiguration? config = null)
    {
        var task = new BaseItemAnalyzerTask(
            NullLogger<BaseItemAnalyzerTask>.Instance,
            NullLoggerFactory.Instance,
            null!, // ILibraryManager, unused by CreateAnalyzerPipeline
            null!, // IProviderManager
            null!, // IFileSystem
            null!, // MediaSegmentUpdateManager
            null!, // IFFmpegCapabilityService
            null!, // IDetectionCacheService, stored but not invoked during construction
            null!); // IMediaDetectionService

        if (config is not null)
        {
            typeof(BaseItemAnalyzerTask)
                .GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(task, config);
        }

        return task;
    }

    private static string[] TypeNames(IReadOnlyList<IMediaFileAnalyzer> analyzers) =>
        analyzers.Select(a => a.GetType().Name).ToArray();

    // ---- Introduction mode ----

    [Theory]
    [InlineData(QueuedMediaCategory.Episode, true, new[] { "ChapterAnalyzer", "ChromaprintAnalyzer" })]
    [InlineData(QueuedMediaCategory.Episode, false, new[] { "ChapterAnalyzer" })]
    [InlineData(QueuedMediaCategory.AnimeEpisode, true, new[] { "ChapterAnalyzer", "ChromaprintAnalyzer" })]
    [InlineData(QueuedMediaCategory.AnimeEpisode, false, new[] { "ChapterAnalyzer" })]
    [InlineData(QueuedMediaCategory.Movie, true, new[] { "ChapterAnalyzer" })]
    [InlineData(QueuedMediaCategory.Movie, false, new[] { "ChapterAnalyzer" })]
    public void Introduction_ProducesExpectedPipeline(
        QueuedMediaCategory category, bool ffmpegValid, string[] expected)
    {
        var task = CreateTask();
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Introduction, category, ffmpegValid, AnalyzerAction.Default);
        Assert.Equal(expected, TypeNames(pipeline));
    }

    // ---- Credits mode ----

    [Theory]
    [InlineData(QueuedMediaCategory.Episode, true,
        new[] { "ChapterAnalyzer", "BlackFrameAnalyzer", "ChromaprintAnalyzer" })]
    [InlineData(QueuedMediaCategory.Episode, false,
        new[] { "ChapterAnalyzer", "BlackFrameAnalyzer" })]
    [InlineData(QueuedMediaCategory.AnimeEpisode, true,
        new[] { "ChapterAnalyzer", "ChromaprintAnalyzer", "BlackFrameAnalyzer" })]
    [InlineData(QueuedMediaCategory.AnimeEpisode, false,
        new[] { "ChapterAnalyzer", "BlackFrameAnalyzer" })]
    [InlineData(QueuedMediaCategory.Movie, true,
        new[] { "ChapterAnalyzer", "BlackFrameAnalyzer" })]
    [InlineData(QueuedMediaCategory.Movie, false,
        new[] { "ChapterAnalyzer", "BlackFrameAnalyzer" })]
    public void Credits_ProducesExpectedPipeline(
        QueuedMediaCategory category, bool ffmpegValid, string[] expected)
    {
        var task = CreateTask();
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Credits, category, ffmpegValid, AnalyzerAction.Default);
        Assert.Equal(expected, TypeNames(pipeline));
    }

    // ---- Chapter-only modes (Recap, Preview, Commercial) ----

    [Theory]
    [InlineData(AnalysisMode.Recap)]
    [InlineData(AnalysisMode.Preview)]
    [InlineData(AnalysisMode.Commercial)]
    public void ChapterOnlyModes_ProduceOnlyChapterAnalyzer(AnalysisMode mode)
    {
        var task = CreateTask();
        var pipeline = task.CreateAnalyzerPipeline(
            mode, QueuedMediaCategory.Episode, ffmpegValid: true, AnalyzerAction.Default);
        Assert.Equal(new[] { "ChapterAnalyzer" }, TypeNames(pipeline));
    }

    // ---- AnalyzerAction promotion overrides ----
    // Base case: Credits + Episode + ffmpegValid=true → [Chapter, BlackFrame, Chromaprint]

    [Fact]
    public void Action_Chapter_KeepsChapterFirst()
    {
        var task = CreateTask();
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Credits, QueuedMediaCategory.Episode, true, AnalyzerAction.Chapter);

        // Chapter is already first, so the order is unchanged.
        Assert.Equal(
            new[] { "ChapterAnalyzer", "BlackFrameAnalyzer", "ChromaprintAnalyzer" },
            TypeNames(pipeline));
    }

    [Fact]
    public void Action_Chromaprint_PromotesChromaprintToFront()
    {
        var task = CreateTask();
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Credits, QueuedMediaCategory.Episode, true, AnalyzerAction.Chromaprint);

        Assert.Equal(
            new[] { "ChromaprintAnalyzer", "ChapterAnalyzer", "BlackFrameAnalyzer" },
            TypeNames(pipeline));
    }

    [Fact]
    public void Action_BlackFrame_PromotesBlackFrameToFront()
    {
        var task = CreateTask();
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Credits, QueuedMediaCategory.Episode, true, AnalyzerAction.BlackFrame);

        Assert.Equal(
            new[] { "BlackFrameAnalyzer", "ChapterAnalyzer", "ChromaprintAnalyzer" },
            TypeNames(pipeline));
    }

    // ---- PreferChromaprint config ----

    [Fact]
    public void PreferChromaprint_WithDefaultAction_PromotesChromaprint()
    {
        var config = new PluginConfiguration { PreferChromaprint = true };
        var task = CreateTask(config);
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Credits, QueuedMediaCategory.Episode, true, AnalyzerAction.Default);

        Assert.Equal(
            new[] { "ChromaprintAnalyzer", "ChapterAnalyzer", "BlackFrameAnalyzer" },
            TypeNames(pipeline));
    }

    [Fact]
    public void PreferChromaprint_WithExplicitAction_ActionTakesPrecedence()
    {
        var config = new PluginConfiguration { PreferChromaprint = true };
        var task = CreateTask(config);
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Credits, QueuedMediaCategory.Episode, true, AnalyzerAction.Chapter);

        // Explicit action=Chapter overrides PreferChromaprint.
        Assert.Equal(
            new[] { "ChapterAnalyzer", "BlackFrameAnalyzer", "ChromaprintAnalyzer" },
            TypeNames(pipeline));
    }

    [Fact]
    public void PreferChromaprint_WhenFfmpegInvalid_NothingToPromote()
    {
        var config = new PluginConfiguration { PreferChromaprint = true };
        var task = CreateTask(config);
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Credits, QueuedMediaCategory.Episode, false, AnalyzerAction.Default);

        // No ChromaprintAnalyzer was added (ffmpegValid=false), so PreferChromaprint is a no-op.
        Assert.Equal(
            new[] { "ChapterAnalyzer", "BlackFrameAnalyzer" },
            TypeNames(pipeline));
    }

    [Fact]
    public void PreferChromaprint_IntroductionMode_PromotesChromaprint()
    {
        var config = new PluginConfiguration { PreferChromaprint = true };
        var task = CreateTask(config);
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Introduction, QueuedMediaCategory.Episode, true, AnalyzerAction.Default);

        Assert.Equal(
            new[] { "ChromaprintAnalyzer", "ChapterAnalyzer" },
            TypeNames(pipeline));
    }

    // ---- UseAlternativeBlackFrameAnalyzer config ----

    [Fact]
    public void UseAltBlackFrame_True_ReturnsBlackFrameAltAnalyzer()
    {
        var config = new PluginConfiguration { UseAlternativeBlackFrameAnalyzer = true };
        var task = CreateTask(config);
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Credits, QueuedMediaCategory.Episode, true, AnalyzerAction.Default);

        Assert.Equal(
            new[] { "ChapterAnalyzer", "BlackFrameAltAnalyzer", "ChromaprintAnalyzer" },
            TypeNames(pipeline));
    }

    [Fact]
    public void UseAltBlackFrame_False_ReturnsBlackFrameAnalyzer()
    {
        var config = new PluginConfiguration { UseAlternativeBlackFrameAnalyzer = false };
        var task = CreateTask(config);
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Credits, QueuedMediaCategory.Episode, true, AnalyzerAction.Default);

        Assert.Equal(
            new[] { "ChapterAnalyzer", "BlackFrameAnalyzer", "ChromaprintAnalyzer" },
            TypeNames(pipeline));
    }

    [Fact]
    public void Action_BlackFrame_PromotesAltBlackFrameAnalyzer()
    {
        var config = new PluginConfiguration { UseAlternativeBlackFrameAnalyzer = true };
        var task = CreateTask(config);
        var pipeline = task.CreateAnalyzerPipeline(
            AnalysisMode.Credits, QueuedMediaCategory.Episode, true, AnalyzerAction.BlackFrame);

        // PromoteAnalyzer matches both BlackFrameAnalyzer and BlackFrameAltAnalyzer.
        Assert.Equal(
            new[] { "BlackFrameAltAnalyzer", "ChapterAnalyzer", "ChromaprintAnalyzer" },
            TypeNames(pipeline));
    }
}
