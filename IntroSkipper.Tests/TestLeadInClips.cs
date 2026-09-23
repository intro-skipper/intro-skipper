// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The lead-in probe through the real decoder on two generated clips, 640 by 480 at 24 fps with
/// four-second GOPs and no scene-cut keyframes. Both nominate the same boundary, the keyframes at
/// 16 and 20 s, through the 90th percentile over a background that is black throughout, so the
/// probe has no crossing to trim to and the prefix's own content decides.
/// </summary>
public class TestLeadInClips
{
    [FactSkipFFmpegTests]
    public async Task MovingStoryOverBlack_KeepsThePolicyStart()
    {
        // 4 to 18 s: a moving lit object over a black background; 18 to 19.5 s blank black; then
        // lettering. Nothing in the prefix is lettered, so the scene starts at the level keyframe
        // rather than at the first frame after the lighter one.
        var credits = await CreditsOf("video/moving-story.mp4");

        Assert.Equal(20, credits?.Start ?? -1, 2);
    }

    [FactSkipFFmpegTests]
    public async Task DensePageThenSparsePage_KeepsTheCreditsFromTheirFirstFrame()
    {
        // 4 to 18 s: fourteen lines of lettering on black, dense enough to lift the 90th percentile;
        // then a shorter page. The second before the lighter keyframe is rows of glyphs, and the
        // spacing between the lines is page, not bars, so the prefix keeps.
        var credits = await CreditsOf("video/dense-sparse.mp4");

        Assert.Equal(4, credits?.Start ?? -1, 2);
    }

    private static async Task<Segment?> CreditsOf(string relativePath)
    {
        var episode = FfmpegTestHelpers.QueueFile(relativePath);
        episode.Duration = 39;
        episode.CreditsFingerprintStart = 0;
        episode.CreditsFingerprintEnd = 39;
        var analyzer = new KeyframeAnalyzer(NullLogger<KeyframeAnalyzer>.Instance, FfmpegTestHelpers.CreateFFmpegService(), new PluginConfiguration { RefineCreditsBoundary = false });

        var candidates = await analyzer.DetectCreditsAsync(episode, 85, 32, 15, detectCardCredits: false);

        return candidates.SingleOrDefault(c => c.Source == SegmentSource.BlackFrame).Segment;
    }
}
