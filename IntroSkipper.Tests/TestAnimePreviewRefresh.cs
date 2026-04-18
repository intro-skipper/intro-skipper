// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using IntroSkipper.Data;
using IntroSkipper.ScheduledTasks;
using Xunit;

namespace IntroSkipper.Tests;

public class TestAnimePreviewRefresh
{
    private static readonly Guid EpisodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const double EpisodeDuration = 1320.0; // 22 minutes

    [Fact]
    public void ReturnsNewSegment_WhenNoPreviewExistsYet()
    {
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Credits] = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0)),
        };

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, timestamps);

        Assert.NotNull(result);
        Assert.Equal(EpisodeId, result!.EpisodeId);
        Assert.Equal(1260.0, result.Start);
        Assert.Equal(EpisodeDuration, result.End);
    }

    [Fact]
    public void RefreshesPreview_WhenCreditsEndShifts()
    {
        // Scenario: previous analysis produced Credits ending at 1080s and a Preview [1080, 1320].
        // A settings change triggered re-analysis, Credits is now [..., 1260] — the old Preview is stale.
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Credits] = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0)),
            [AnalysisMode.Preview] = new Segment(EpisodeId, new TimeRange(1080.0, EpisodeDuration)),
        };

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, timestamps);

        Assert.NotNull(result);
        Assert.Equal(1260.0, result!.Start);
        Assert.Equal(EpisodeDuration, result.End);
    }

    [Fact]
    public void IsIdempotent_WhenExistingPreviewMatchesCreditsEnd()
    {
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Credits] = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0)),
            [AnalysisMode.Preview] = new Segment(EpisodeId, new TimeRange(1260.0, EpisodeDuration)),
        };

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, timestamps);

        Assert.Null(result);
    }

    [Fact]
    public void TreatsSubSecondDriftAsEqual()
    {
        // Chromaprint quantises timestamps to ~0.124s — a 0.3s delta between runs is noise, not a real change.
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Credits] = new Segment(EpisodeId, new TimeRange(1200.0, 1260.3)),
            [AnalysisMode.Preview] = new Segment(EpisodeId, new TimeRange(1260.0, EpisodeDuration)),
        };

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, timestamps);

        Assert.Null(result);
    }

    [Fact]
    public void RefreshesPreview_WhenDriftExceedsTolerance()
    {
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Credits] = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0)),
            [AnalysisMode.Preview] = new Segment(EpisodeId, new TimeRange(1259.0, EpisodeDuration)),
        };

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, timestamps);

        Assert.NotNull(result);
        Assert.Equal(1260.0, result!.Start);
    }

    [Fact]
    public void ReturnsNull_WhenNoCreditsPresent()
    {
        var timestamps = new Dictionary<AnalysisMode, Segment>();

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, timestamps);

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenCreditsAreInvalid()
    {
        // A default-constructed Segment has End == 0 → Valid is false.
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Credits] = new Segment(EpisodeId),
        };

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, timestamps);

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenCreditsReachEndOfEpisode()
    {
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Credits] = new Segment(EpisodeId, new TimeRange(1200.0, EpisodeDuration)),
        };

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, timestamps);

        Assert.Null(result);
    }

    [Fact]
    public void IgnoresInvalidExistingPreview()
    {
        // A stored Preview with End==0 is invalid (e.g. leftover DB row); should be overwritten.
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Credits] = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0)),
            [AnalysisMode.Preview] = new Segment(EpisodeId),
        };

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, timestamps);

        Assert.NotNull(result);
        Assert.Equal(1260.0, result!.Start);
        Assert.Equal(EpisodeDuration, result.End);
    }
}
