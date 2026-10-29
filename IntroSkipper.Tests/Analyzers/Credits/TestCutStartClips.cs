// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The cut start through the real decoder on generated clips, 640 by 480 at 24 fps with
/// four-second GOPs and no scene-cut keyframes. In both, 4 to 17 s is a moving lit object over a
/// dark grey background, the background drops to black at 17 s, and the keyframes at 16 and 20 s
/// bound the gap the cut start decodes.
/// </summary>
public class TestCutStartClips
{
    [FactSkipFFmpegTests]
    public async Task SubjectMovingOnAfterTheBackgroundDrops_StartsWhereTheLetteringAppears()
    {
        // 17 to 18 s the object keeps moving over black; 18 to 19.5 s blank black; then lettering.
        // The background dropping at 17 s and the object leaving at 18 s each change less of the
        // picture than the lettering appearing at 19.5 s, which carries over half the change between
        // the keyframes, so the credits start there and not at 17 s inside the story.
        var credits = Assert.Single(await CreditsOf(FfmpegTestHelpers.QueueFile("video/moving-story.mp4")));

        Assert.Equal(19.5, credits.Start, 3);
    }

    [FactSkipFFmpegTests]
    public async Task CutToBlackBetweenKeyframes_StartsAtTheCut()
    {
        // 17 to 17.5 s the object over black; 17.5 to 21 s blank black; then lettering. The object
        // leaving at 17.5 s is the largest change between the keyframes, so the start moves back
        // from the blank keyframe at 20 s to 17.5 s. The same stream remuxed to MPEG-TS and scanned from an offset between keyframes lands on
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
        var analyzer = new KeyframeAnalyzer(
            NullLogger<KeyframeAnalyzer>.Instance,
            FfmpegTestHelpers.CreateFFmpegService(),
            DatabaseTestHelpers.CreateTempCacheService(),
            new PluginConfiguration { RefineCreditsBoundary = true, BlackFrameThreshold = 32, DetectNonBlackCredits = false });

        var candidates = await analyzer.DetectCreditsAsync(episode, CancellationToken.None);

        return [.. candidates.Where(c => c.Source == SegmentSource.BlackFrame).Select(c => c.Segment)];
    }
}
