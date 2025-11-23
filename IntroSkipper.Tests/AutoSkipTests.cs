// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using IntroSkipper.Data;
using IntroSkipper.Services;
using Xunit;

namespace IntroSkipper.Tests;

public class TestAutoSkip
{
    [Theory]
    [InlineData("Skipping %segmenttype", AnalysisMode.Introduction, 10.6, 70.2, "Skipping Introduction")]
    [InlineData("%segmenttype from %start to %end", AnalysisMode.Credits, 65.7, 125.2, "Credits from 66 to 125")]
    [InlineData("%segmenttype detected (%duration seconds)", AnalysisMode.Recap, 30.4, 90.2, "Recap detected (60 seconds)")]
    [InlineData("", AnalysisMode.Preview, 10, 20, "")]
    [InlineData("Now skipping %segmenttype", AnalysisMode.Commercial, 5.1, 35.9, "Now skipping Commercial")]
    [InlineData(null, AnalysisMode.Introduction, 10, 20, "")]
    public void FormatNotificationText_ReplacesPlaceholdersCorrectly(string? template, AnalysisMode segmentType, double start, double end, string expected)
    {
        // Act
        var result = AutoSkip.FormatNotificationText(template, segmentType, start, end);

        // Assert
        Assert.Equal(expected, result);
    }
}
