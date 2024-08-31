namespace ConfusedPolarBear.Plugin.IntroSkipper.Tests;

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
}
