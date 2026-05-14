// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using IntroSkipper.Data;
using Jellyfin.Database.Implementations.Enums;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// Round-trip and alias tests for <see cref="AnalysisModeExtensions"/>.
/// Ensures the centralized mapping is the single source of truth and that
/// adding a new <see cref="AnalysisMode"/> without updating the mapping fails a focused test.
/// </summary>
public class TestAnalysisModeMapping
{
    // ---- AnalysisMode → MediaSegmentType ----

    [Theory]
    [InlineData(AnalysisMode.Introduction, MediaSegmentType.Intro)]
    [InlineData(AnalysisMode.Credits, MediaSegmentType.Outro)]
    [InlineData(AnalysisMode.Preview, MediaSegmentType.Preview)]
    [InlineData(AnalysisMode.Recap, MediaSegmentType.Recap)]
    [InlineData(AnalysisMode.Commercial, MediaSegmentType.Commercial)]
    public void ToMediaSegmentType_MapsCorrectly(AnalysisMode mode, MediaSegmentType expected)
    {
        Assert.Equal(expected, mode.ToMediaSegmentType());
    }

    // ---- MediaSegmentType → AnalysisMode ----

    [Theory]
    [InlineData(MediaSegmentType.Intro, AnalysisMode.Introduction)]
    [InlineData(MediaSegmentType.Outro, AnalysisMode.Credits)]
    [InlineData(MediaSegmentType.Preview, AnalysisMode.Preview)]
    [InlineData(MediaSegmentType.Recap, AnalysisMode.Recap)]
    [InlineData(MediaSegmentType.Commercial, AnalysisMode.Commercial)]
    public void ToAnalysisMode_MapsCorrectly(MediaSegmentType type, AnalysisMode expected)
    {
        Assert.Equal(expected, type.ToAnalysisMode());
    }

    // ---- Round-trip: every AnalysisMode survives AnalysisMode → MediaSegmentType → AnalysisMode ----

    [Theory]
    [InlineData(AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Credits)]
    [InlineData(AnalysisMode.Preview)]
    [InlineData(AnalysisMode.Recap)]
    [InlineData(AnalysisMode.Commercial)]
    public void RoundTrip_AnalysisMode_Survives(AnalysisMode mode)
    {
        var segmentType = mode.ToMediaSegmentType();
        var roundTripped = segmentType.ToAnalysisMode();
        Assert.Equal(mode, roundTripped);
    }

    // ---- All enum values are covered (catches new values added without updating mappings) ----

    [Fact]
    public void AllAnalysisModes_HaveMediaSegmentTypeMapping()
    {
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            var ex = Record.Exception(() => mode.ToMediaSegmentType());
            Assert.Null(ex);
        }
    }

    // ---- String parsing ----

    [Theory]
    [InlineData("intro", AnalysisMode.Introduction)]
    [InlineData("Intro", AnalysisMode.Introduction)]
    [InlineData("INTRO", AnalysisMode.Introduction)]
    [InlineData("recap", AnalysisMode.Recap)]
    [InlineData("preview", AnalysisMode.Preview)]
    [InlineData("outro", AnalysisMode.Credits)]
    [InlineData("credits", AnalysisMode.Credits)]
    [InlineData("Credits", AnalysisMode.Credits)]
    [InlineData("Outro", AnalysisMode.Credits)]
    [InlineData("commercial", AnalysisMode.Commercial)]
    [InlineData("Commercial", AnalysisMode.Commercial)]
    public void ParseSegmentType_RecognizedStrings(string input, AnalysisMode expected)
    {
        Assert.Equal(expected, AnalysisModeExtensions.ParseSegmentType(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("introduction")]
    [InlineData("credit")]
    public void ParseSegmentType_UnknownStrings_Throws(string input)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalysisModeExtensions.ParseSegmentType(input));
    }

    // ---- TryToMediaSegmentType ----

    [Theory]
    [InlineData(AnalysisMode.Introduction, MediaSegmentType.Intro)]
    [InlineData(AnalysisMode.Credits, MediaSegmentType.Outro)]
    [InlineData(AnalysisMode.Preview, MediaSegmentType.Preview)]
    [InlineData(AnalysisMode.Recap, MediaSegmentType.Recap)]
    [InlineData(AnalysisMode.Commercial, MediaSegmentType.Commercial)]
    public void TryToMediaSegmentType_KnownModes_ReturnsTrueWithCorrectType(AnalysisMode mode, MediaSegmentType expected)
    {
        var result = mode.TryToMediaSegmentType(out var type);

        Assert.True(result);
        Assert.Equal(expected, type);
    }

    [Fact]
    public void TryToMediaSegmentType_UnknownMode_ReturnsFalse()
    {
        var unknown = (AnalysisMode)(-1);

        var result = unknown.TryToMediaSegmentType(out _);

        Assert.False(result);
    }
}
