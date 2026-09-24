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
/// The lead-in probe through the real decoder on a generated clip, 640 by 480 at 24 fps with
/// four-second GOPs and no scene-cut keyframes.
/// </summary>
public class TestLeadInClips
{
    [FactSkipFFmpegTests]
    public async Task MovingStoryOverBlack_KeepsThePolicyStart()
    {
        // 4 to 18 s: a moving lit object over a black background; 18 to 19.5 s blank black; then
        // lettering. The 90th percentile nominates the keyframes at 16 and 20 s, but the background
        // is black throughout and never crosses, so the scene starts at the level keyframe rather
        // than at the first frame after the lighter one.
        var credits = await CreditsOf("video/moving-story.mp4");

        Assert.Equal(20, credits?.Start ?? -1, 2);
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
