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
    public async Task SubjectMovingOnAfterTheBackgroundDrops_KeepsTheKeyframeStart()
    {
        // 4 to 17 s: a moving lit object over a dark grey background; 17 to 18 s the object keeps
        // moving over black; 18 to 19.5 s blank black; then lettering. The keyframes at 16 and 20 s
        // bound the lead-in. The lettering before 20 s is coded again at the keyframe and differs at
        // the edges of its glyphs, so the start stays at 20 s, not at 17 s inside the story.
        var credits = await CreditsOf("video/moving-story.mp4");

        Assert.Equal(20, credits?.Start ?? -1, 2);
    }

    private static async Task<Segment?> CreditsOf(string relativePath)
    {
        var episode = FfmpegTestHelpers.QueueFile(relativePath);
        episode.Duration = 39;
        episode.CreditsFingerprintStart = 0;
        episode.CreditsFingerprintEnd = 39;
        var analyzer = new KeyframeAnalyzer(NullLogger<KeyframeAnalyzer>.Instance, FfmpegTestHelpers.CreateFFmpegService(), new PluginConfiguration { RefineCreditsBoundary = true });

        var candidates = await analyzer.DetectCreditsAsync(episode, 85, 32, 15, detectCardCredits: false);

        return candidates.SingleOrDefault(c => c.Source == SegmentSource.BlackFrame).Segment;
    }
}
