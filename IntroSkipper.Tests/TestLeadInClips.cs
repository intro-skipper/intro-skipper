// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The lead-in probe through the real decoder on generated clips, 640 by 480 at 24 fps with
/// four-second GOPs and no scene-cut keyframes. In both, 4 to 17 s is a moving lit object over a
/// dark grey background, and the keyframes at 16 and 20 s bound the lead-in.
/// </summary>
public class TestLeadInClips
{
    [FactSkipFFmpegTests]
    public async Task SubjectMovingOnAfterTheBackgroundDrops_KeepsTheKeyframeStart()
    {
        // 17 to 18 s the object keeps moving over black; 18 to 19.5 s blank black; then lettering.
        // The lettering before 20 s is coded again at the keyframe and differs at the edges of its
        // glyphs, so the start stays at 20 s, not at 17 s inside the story.
        var credits = Assert.Single(await CreditsOf(FfmpegTestHelpers.QueueFile("video/moving-story.mp4")));

        Assert.Equal(20, credits.Start, 2);
    }

    [FactSkipFFmpegTests]
    public async Task CutToBlackBetweenKeyframes_StartsAtTheCut()
    {
        // 17 to 17.5 s the object over black; 17.5 to 21 s blank black; then lettering. Every frame
        // from 17.5 s to the blank keyframe at 20 s matches it, so the start moves back to 17.5 s.
        // The same stream remuxed to MPEG-TS and scanned from an offset between keyframes lands on
        // the same frame: the decode's frame times sit on the scan's timeline.
        var clip = FfmpegTestHelpers.QueueFile("video/cut-to-black.mp4");
        Assert.Equal(17.5, Assert.Single(await CreditsOf(clip)).Start, 3);

        var ts = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-cut-to-black.ts");
        try
        {
            await new FFmpegProcessRunner(NullLogger.Instance).RunAsync("ffmpeg", ["-y", "-v", "error", "-i", clip.Path, "-c", "copy", ts]);
            var remuxed = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Name = "cut-to-black.ts", Path = ts };

            Assert.Equal(17.5, Assert.Single(await CreditsOf(remuxed, fingerprintStart: 3.999)).Start, 3);
        }
        finally
        {
            File.Delete(ts);
        }
    }

    private static async Task<List<Segment>> CreditsOf(QueuedEpisode episode, double fingerprintStart = 0)
    {
        episode.Duration = 39;
        episode.CreditsFingerprintStart = fingerprintStart;
        episode.CreditsFingerprintEnd = 39;
        var analyzer = new KeyframeAnalyzer(NullLogger<KeyframeAnalyzer>.Instance, FfmpegTestHelpers.CreateFFmpegService(), DatabaseTestHelpers.CreateTempCacheService(), new PluginConfiguration { RefineCreditsBoundary = true });

        var candidates = await analyzer.DetectCreditsAsync(episode, 85, 32, 15, detectCardCredits: false);

        return [.. candidates.Where(c => c.Source == SegmentSource.BlackFrame).Select(c => c.Segment)];
    }
}
