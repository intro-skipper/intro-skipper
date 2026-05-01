// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.ScheduledTasks;
using Xunit;
using MediaFileAnalyzerKind = IntroSkipper.ScheduledTasks.BaseItemAnalyzerTask.MediaFileAnalyzerKind;

namespace IntroSkipper.Tests;

public class TestAnalyzerOrder
{
    [Fact]
    public void CreditsEpisodeWithFfmpegValidUsesChapterBlackFrameChromaprint()
    {
        AssertCreditsOrder(
            QueuedMediaCategory.Episode,
            AnalyzerAction.Default,
            false,
            MediaFileAnalyzerKind.Chapter,
            MediaFileAnalyzerKind.BlackFrame,
            MediaFileAnalyzerKind.Chromaprint);
    }

    [Fact]
    public void CreditsAnimeEpisodeWithFfmpegValidUsesChapterChromaprintBlackFrame()
    {
        AssertCreditsOrder(
            QueuedMediaCategory.AnimeEpisode,
            AnalyzerAction.Default,
            false,
            MediaFileAnalyzerKind.Chapter,
            MediaFileAnalyzerKind.Chromaprint,
            MediaFileAnalyzerKind.BlackFrame);
    }

    [Fact]
    public void CreditsMovieWithFfmpegValidUsesChapterBlackFrame()
    {
        AssertCreditsOrder(
            QueuedMediaCategory.Movie,
            AnalyzerAction.Default,
            false,
            MediaFileAnalyzerKind.Chapter,
            MediaFileAnalyzerKind.BlackFrame);
    }

    [Theory]
    [InlineData(AnalyzerAction.Default, true)]
    [InlineData(AnalyzerAction.Chromaprint, false)]
    [InlineData(AnalyzerAction.Chromaprint, true)]
    public void CreditsEpisodeOverridesDoNotReorderCreditsAnalyzers(
        AnalyzerAction action,
        bool preferChromaprint)
    {
        AssertCreditsOrder(
            QueuedMediaCategory.Episode,
            action,
            preferChromaprint,
            MediaFileAnalyzerKind.Chapter,
            MediaFileAnalyzerKind.BlackFrame,
            MediaFileAnalyzerKind.Chromaprint);
    }

    [Fact]
    public void CreditsAnimeBlackFrameActionDoesNotReorderCreditsAnalyzers()
    {
        AssertCreditsOrder(
            QueuedMediaCategory.AnimeEpisode,
            AnalyzerAction.BlackFrame,
            false,
            MediaFileAnalyzerKind.Chapter,
            MediaFileAnalyzerKind.Chromaprint,
            MediaFileAnalyzerKind.BlackFrame);
    }

    [Fact]
    public void CreditsMovieChromaprintActionDoesNotAddChromaprint()
    {
        AssertCreditsOrder(
            QueuedMediaCategory.Movie,
            AnalyzerAction.Chromaprint,
            false,
            MediaFileAnalyzerKind.Chapter,
            MediaFileAnalyzerKind.BlackFrame);
    }

    private static void AssertCreditsOrder(
        QueuedMediaCategory category,
        AnalyzerAction action,
        bool preferChromaprint,
        params MediaFileAnalyzerKind[] expected)
    {
        var actual = BaseItemAnalyzerTask.GetAnalyzerOrder(
            AnalysisMode.Credits,
            category,
            true,
            action,
            preferChromaprint);

        Assert.Equal(expected, actual);
    }
}
