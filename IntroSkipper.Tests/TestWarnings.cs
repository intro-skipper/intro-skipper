// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2025 AbandonedCart
// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

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
        WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
        Assert.Equal("IncompatibleFFmpegBuild", WarningManager.GetWarnings());
        Assert.True(WarningManager.HasFlag(PluginWarning.IncompatibleFFmpegBuild));
    }

    [Fact]
    public void TestDoubleFlagSerialization()
    {
        WarningManager.Clear();
        WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
        WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);
        WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);
        Assert.True(WarningManager.HasFlag(PluginWarning.IncompatibleFFmpegBuild) && WarningManager.HasFlag(PluginWarning.InvalidChromaprintFingerprint));
        Assert.Equal(
            "InvalidChromaprintFingerprint, IncompatibleFFmpegBuild",
            WarningManager.GetWarnings());
    }

    [Fact]
    public void TestHasFlag()
    {
        WarningManager.Clear();
        Assert.True(WarningManager.HasFlag(PluginWarning.None));
        Assert.False(WarningManager.HasFlag(PluginWarning.IncompatibleFFmpegBuild) && WarningManager.HasFlag(PluginWarning.InvalidChromaprintFingerprint));
        WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
        WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);
        Assert.True(WarningManager.HasFlag(PluginWarning.IncompatibleFFmpegBuild) && WarningManager.HasFlag(PluginWarning.InvalidChromaprintFingerprint));
        Assert.True(WarningManager.HasFlag(PluginWarning.IncompatibleFFmpegBuild));
        Assert.True(WarningManager.HasFlag(PluginWarning.None));
    }
}
