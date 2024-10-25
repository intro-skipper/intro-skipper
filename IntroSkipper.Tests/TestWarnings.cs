// Copyright (C) 2024 Intro-Skipper Contributors <intro-skipper.org>
// SPDX-License-Identifier: GNU General Public License v3.0 only.

namespace IntroSkipper.Tests;

using IntroSkipper.Data;
using Xunit;

public class TestFlags
{
    [Fact]
    public void TestEmptyFlagSerialization()
    {
        WarningManager.Clear();
        Assert.Equal("None", WarningManager.GetWarnings());
    }

    [Fact]
    public void TestSingleFlagSerialization()
    {
        WarningManager.Clear();
        WarningManager.SetFlag(PluginWarning.UnableToAddSkipButton);
        Assert.Equal("UnableToAddSkipButton", WarningManager.GetWarnings());
        Assert.True(WarningManager.HasFlag(PluginWarning.UnableToAddSkipButton));
    }

    [Fact]
    public void TestDoubleFlagSerialization()
    {
        WarningManager.Clear();
        WarningManager.SetFlag(PluginWarning.UnableToAddSkipButton);
        WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);
        WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);
        Assert.True(WarningManager.HasFlag(PluginWarning.UnableToAddSkipButton) && WarningManager.HasFlag(PluginWarning.InvalidChromaprintFingerprint));
        Assert.Equal(
            "UnableToAddSkipButton, InvalidChromaprintFingerprint",
            WarningManager.GetWarnings());
    }

    [Fact]
    public void TestHasFlag()
    {
        WarningManager.Clear();
        Assert.True(WarningManager.HasFlag(PluginWarning.None));
        Assert.False(WarningManager.HasFlag(PluginWarning.UnableToAddSkipButton) && WarningManager.HasFlag(PluginWarning.InvalidChromaprintFingerprint));
        WarningManager.SetFlag(PluginWarning.UnableToAddSkipButton);
        WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);
        Assert.True(WarningManager.HasFlag(PluginWarning.UnableToAddSkipButton) && WarningManager.HasFlag(PluginWarning.InvalidChromaprintFingerprint));
        Assert.False(WarningManager.HasFlag(PluginWarning.IncompatibleFFmpegBuild));
        Assert.True(WarningManager.HasFlag(PluginWarning.None));
    }
}
