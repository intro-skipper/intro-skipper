// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
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
        var credits = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0));

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, credits, []);

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
        var credits = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0));
        var stalePreview = new Segment(EpisodeId, new TimeRange(1080.0, EpisodeDuration));

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, credits, [stalePreview]);

        Assert.NotNull(result);
        Assert.Equal(1260.0, result!.Start);
        Assert.Equal(EpisodeDuration, result.End);
    }

    [Fact]
    public void IsIdempotent_WhenExistingPreviewMatchesCreditsEnd()
    {
        var credits = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0));
        var matchingPreview = new Segment(EpisodeId, new TimeRange(1260.0, EpisodeDuration));

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, credits, [matchingPreview]);

        Assert.Null(result);
    }

    [Fact]
    public void TreatsSubSecondDriftAsEqual()
    {
        // Chromaprint quantises timestamps to ~0.124s — a 0.3s delta between runs is noise, not a real change.
        var credits = new Segment(EpisodeId, new TimeRange(1200.0, 1260.3));
        var existingPreview = new Segment(EpisodeId, new TimeRange(1260.0, EpisodeDuration));

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, credits, [existingPreview]);

        Assert.Null(result);
    }

    [Fact]
    public void TreatsExactBoundaryDriftAsEqual()
    {
        // A drift of exactly 0.5s should be treated as equal (matches the "within or equal to" doc).
        var credits = new Segment(EpisodeId, new TimeRange(1200.0, 1260.5));
        var existingPreview = new Segment(EpisodeId, new TimeRange(1260.0, EpisodeDuration));

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, credits, [existingPreview]);

        Assert.Null(result);
    }

    [Fact]
    public void RefreshesPreview_WhenEpisodeDurationChanges()
    {
        // Episode file replaced with a longer cut: credits.End is unchanged but Preview.End is stale.
        var credits = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0));
        var stalePreview = new Segment(EpisodeId, new TimeRange(1260.0, EpisodeDuration));

        const double newDuration = EpisodeDuration + 60.0;
        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, newDuration, credits, [stalePreview]);

        Assert.NotNull(result);
        Assert.Equal(1260.0, result!.Start);
        Assert.Equal(newDuration, result.End);
    }

    [Fact]
    public void RefreshesPreview_WhenDriftExceedsTolerance()
    {
        var credits = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0));
        var stalePreview = new Segment(EpisodeId, new TimeRange(1259.0, EpisodeDuration));

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, credits, [stalePreview]);

        Assert.NotNull(result);
        Assert.Equal(1260.0, result!.Start);
    }

    [Fact]
    public void ReturnsNull_WhenNoCreditsPresent()
    {
        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, credits: null, existingPreviews: []);

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenCreditsAreInvalid()
    {
        // A default-constructed Segment has End == 0 → Valid is false.
        var credits = new Segment(EpisodeId);

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, credits, []);

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenCreditsReachEndOfEpisode()
    {
        var credits = new Segment(EpisodeId, new TimeRange(1200.0, EpisodeDuration));

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, credits, []);

        Assert.Null(result);
    }

    [Fact]
    public void IgnoresInvalidExistingPreview()
    {
        // A stored Preview with End==0 is invalid (e.g. leftover DB row); should be overwritten.
        var credits = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0));
        var invalidPreview = new Segment(EpisodeId);

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, credits, [invalidPreview]);

        Assert.NotNull(result);
        Assert.Equal(1260.0, result!.Start);
        Assert.Equal(EpisodeDuration, result.End);
    }

    [Fact]
    public void ReturnsNull_WhenAnyExistingPreviewMatchesTolerance()
    {
        // Multiple previews can coexist per (item, type); the tolerance check is satisfied by
        // ANY existing preview, so a stale first entry must not force a rewrite when the
        // second one already matches.
        var credits = new Segment(EpisodeId, new TimeRange(1200.0, 1260.0));
        var stalePreview = new Segment(EpisodeId, new TimeRange(1080.0, EpisodeDuration));
        var matchingPreview = new Segment(EpisodeId, new TimeRange(1260.0, EpisodeDuration));

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, EpisodeDuration, credits, [stalePreview, matchingPreview]);

        Assert.Null(result);
    }
}
