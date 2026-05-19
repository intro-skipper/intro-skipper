// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only


using IntroSkipper.Data;
using Xunit;

namespace IntroSkipper.Tests;

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
    }

    [Fact]
    public void TestDoubleFlagSerialization()
    {
        WarningManager.Clear();
        WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
        WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);
        WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);
        Assert.Equal(
            "InvalidChromaprintFingerprint, IncompatibleFFmpegBuild",
            WarningManager.GetWarnings());
    }

    [Fact]
    public void TestClearSingleFlag()
    {
        WarningManager.Clear();
        WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
        WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);

        WarningManager.ClearFlag(PluginWarning.IncompatibleFFmpegBuild);

        Assert.Equal("InvalidChromaprintFingerprint", WarningManager.GetWarnings());
    }

}
