// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestTimeAdjustmentHelper
{
    private int _silenceScans;
    private int _keyframeScans;

    [Theory]
    [InlineData(2, true, 0, 60, 1.2, 10, 2, 10)]   // start within snap threshold: snapped to 0, then offset applied
    [InlineData(2, true, 0, 60, 0, 10, 2, 10)]     // start at 0: offset applied when opted in
    [InlineData(2, false, 0, 60, 0, 10, 0, 10)]    // start at 0: offset not applied when snapping and option is off
    [InlineData(2, true, 0, 60, 1, 2, 0, 0)]       // offset consumes the whole intro: invalid segment
    [InlineData(2, false, 0, 60, 5, 12, 7, 12)]    // not snapping: offset always applied
    [InlineData(0, false, 100, 30, -5, 200, 0, 30)] // negative start snaps to 0; end offset cannot push end past 0 or duration
    public async Task StartAndEndOffsets(int startOffset, bool includeStartOffsetWhenSnapping, int endOffset, double duration, double start, double end, double expectedStart, double expectedEnd)
    {
        var config = new PluginConfiguration
        {
            EndSnapThreshold = 2.0,
            AdjustIntroBasedOnChapters = false,
            AdjustIntroBasedOnSilence = false,
            SnapToKeyframe = false,
            AdjustWindowInward = 2.0,
            AdjustWindowOutward = 2.0,
            IntroStartOffset = startOffset,
            IncludeIntroStartOffsetWhenSnapping = includeStartOffsetWhenSnapping,
            IntroEndOffset = endOffset,
        };
        var helper = new TimeAdjustmentHelper(NullLogger.Instance, config, AnalysisMode.Introduction, null!);
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = duration };
        var original = new Segment(episode.EpisodeId) { Start = start, End = end };

        var adjusted = await helper.AdjustIntroTimesAsync(episode, original);

        Assert.Equal(expectedStart, adjusted.Start);
        Assert.Equal(expectedEnd, adjusted.End);
        Assert.Equal(expectedEnd > 0, adjusted.Valid);
    }

    [Fact]
    public async Task ChapterSourcedEnd_StaysOnTheMark_AndProbesNothing()
    {
        var adjusted = await AdjustMarkEndAsync(new PluginConfiguration(), chapterSourced: true);

        Assert.Equal((60, 90), (adjusted.Start, adjusted.End));
        Assert.Equal((0, 0), (_silenceScans, _keyframeScans));
    }

    // Without the snaps the offset alone moves the end; with them it would land on the silence at 88.9.
    [Fact]
    public async Task ChapterSourcedEnd_StillTakesTheEndOffset()
    {
        var adjusted = await AdjustMarkEndAsync(new PluginConfiguration { IntroEndOffset = 3 }, chapterSourced: true);

        Assert.Equal(87, adjusted.End);
        Assert.Equal((0, 0), (_silenceScans, _keyframeScans));
    }

    // The end moves to the silence at 88.9, then to the keyframe nearest it.
    [Fact]
    public async Task OffMarkEnd_StillMoves()
    {
        var adjusted = await AdjustMarkEndAsync(new PluginConfiguration(), chapterSourced: false);

        Assert.Equal(89.1, adjusted.End);
        Assert.Equal((1, 1), (_silenceScans, _keyframeScans));
    }

    // An intro from 60 s to a chapter mark at 90 s, with silence at 88.9 to 89.5 s and keyframes at
    // 89.1 and 90.2 s. Each probe returns only what lies in its range, as ffmpeg would.
    private Task<Segment> AdjustMarkEndAsync(PluginConfiguration config, bool chapterSourced)
    {
        var silence = new TimeRange(88.9, 89.5);
        double[] keyframes = [89.1, 90.2];
        var ffmpeg = new StubFFmpegService
        {
            Silence = (_, range, _) =>
            {
                _silenceScans++;
                return range.Intersects(silence) ? [silence] : [];
            },
            KeyFrames = (_, range, _) =>
            {
                _keyframeScans++;
                return [.. keyframes.Where(t => t >= range.Start && t <= range.End)];
            },
        };
        var helper = new TimeAdjustmentHelper(NullLogger.Instance, config, AnalysisMode.Introduction, ffmpeg);
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 1500 };

        return helper.AdjustIntroTimesAsync(episode, new Segment(episode.EpisodeId) { Start = 60, End = 90 }, chapterSourced);
    }
}
