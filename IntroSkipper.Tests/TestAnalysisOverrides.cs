// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Threading.Tasks;
using Xunit;

public sealed class TestAnalysisOverrides
{
    [Fact]
    public async Task SubtitleDetectionOverrides_RoundTripIndependently()
    {
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var seasonId = Guid.NewGuid();

        await database.SetAnalysisOverridesAsync(
            seasonId,
            analysisPercent: null,
            analysisLengthLimit: null,
            previewFromCreditsEnd: null,
            subtitleRecapDetection: true,
            subtitlePreviewDetection: false);

        var overrides = await database.GetAnalysisOverridesAsync(seasonId);

        Assert.True(overrides.SubtitleRecapDetection);
        Assert.False(overrides.SubtitlePreviewDetection);
        Assert.Null(overrides.AnalysisPercent);
        Assert.Null(overrides.AnalysisLengthLimit);
        Assert.Null(overrides.PreviewFromCreditsEnd);
    }
}
