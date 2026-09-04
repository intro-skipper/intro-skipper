// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestTimeAdjustmentHelper
{
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
}
