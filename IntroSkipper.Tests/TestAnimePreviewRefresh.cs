// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using IntroSkipper.Data;
using IntroSkipper.ScheduledTasks;
using Xunit;

namespace IntroSkipper.Tests;

public class TestAnimePreviewRefresh
{
    private static readonly Guid EpisodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const double EpisodeDuration = 1320.0; // 22 minutes

    // Credits always start at 1200; a null creditsEnd means no credits, 0 means an invalid segment.
    // Existing previews are (Start, End) pairs; (0, 0) is an invalid leftover row.
    // expectedStart null means no refresh; otherwise the new preview runs [expectedStart, duration].
    public static TheoryData<double?, double, (double Start, double End)[], double?> Cases => new()
    {
        // No preview yet.
        { 1260, EpisodeDuration, [], 1260 },

        // Credits end shifted after re-analysis: the old preview is stale.
        { 1260, EpisodeDuration, [(1080, EpisodeDuration)], 1260 },

        // Existing preview already matches.
        { 1260, EpisodeDuration, [(1260, EpisodeDuration)], null },

        // Drift of exactly 0.5s is within tolerance (Chromaprint quantizes to ~0.124s).
        { 1260.5, EpisodeDuration, [(1260, EpisodeDuration)], null },

        // Episode replaced with a longer cut: credits unchanged, preview end stale.
        { 1260, EpisodeDuration + 60, [(1260, EpisodeDuration)], 1260 },

        // Drift beyond tolerance.
        { 1260, EpisodeDuration, [(1259, EpisodeDuration)], 1260 },

        // No credits at all.
        { null, EpisodeDuration, [], null },

        // Invalid credits segment (End == 0).
        { 0, EpisodeDuration, [], null },

        // Credits reach the end of the episode: nothing left for a preview.
        { EpisodeDuration, EpisodeDuration, [], null },

        // An invalid stored preview is overwritten.
        { 1260, EpisodeDuration, [(0, 0)], 1260 },

        // Any matching preview suffices; a stale first entry must not force a rewrite.
        { 1260, EpisodeDuration, [(1080, EpisodeDuration), (1260, EpisodeDuration)], null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ComputeAnimePreviewFromCredits(double? creditsEnd, double duration, (double Start, double End)[] existingPreviews, double? expectedStart)
    {
        var credits = creditsEnd is null ? null : new Segment(EpisodeId, new TimeRange(1200.0, creditsEnd.Value));
        var previews = existingPreviews.Select(p => new Segment(EpisodeId, new TimeRange(p.Start, p.End))).ToList();

        var result = BaseItemAnalyzerTask.ComputeAnimePreviewFromCredits(EpisodeId, duration, credits, previews);

        if (expectedStart is null)
        {
            Assert.Null(result);
            return;
        }

        Assert.NotNull(result);
        Assert.Equal(EpisodeId, result.EpisodeId);
        Assert.Equal(expectedStart.Value, result.Start);
        Assert.Equal(duration, result.End);
    }
}
