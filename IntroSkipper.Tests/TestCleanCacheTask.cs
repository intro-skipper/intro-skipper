// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using IntroSkipper.ScheduledTasks;
using Xunit;

public sealed class TestCleanCacheTask
{
    private const string Id = "aabbccdd00112233aabbccdd00112233";

    [Theory]
    [InlineData(Id, null)]
    [InlineData(Id + "-credits", null)]
    public void GetMigratedDbKey_TextFormat_ReturnsNull(string filename, string? expected)
    {
        Assert.Equal(expected, CleanCacheTask.GetMigratedDbKey(filename, Id));
    }

    [Theory]
    [InlineData(Id + "-blackframes-0-v1", Id + "-blackframes-0-v2")]
    [InlineData(Id + "-blackframes-1-v2", Id + "-blackframes-1-v2")]
    [InlineData(Id + "-blackframes-0-credits-v1", Id + "-blackframes-0-credits-v2")]
    public void GetMigratedDbKey_Blackframes(string filename, string expected)
    {
        Assert.Equal(expected, CleanCacheTask.GetMigratedDbKey(filename, Id));
    }

    [Theory]
    [InlineData(Id + "-silence-0-v2", Id + "-silence-0-v3")]
    [InlineData(Id + "-silence-1-v3", Id + "-silence-1-v3")]
    public void GetMigratedDbKey_Silence(string filename, string expected)
    {
        Assert.Equal(expected, CleanCacheTask.GetMigratedDbKey(filename, Id));
    }

    [Theory]
    [InlineData(Id + "-keyframes-0-v1", Id + "-keyframes-0-v2")]
    [InlineData(Id + "-keyframes-1-v2", Id + "-keyframes-1-v2")]
    public void GetMigratedDbKey_Keyframes(string filename, string expected)
    {
        Assert.Equal(expected, CleanCacheTask.GetMigratedDbKey(filename, Id));
    }

    [Theory]
    [InlineData(Id + "-credits-blackframes-90-alt", Id + "-credits-blackframes-90-v2")]
    [InlineData(Id + "-blackframes-0-alt", Id + "-blackframes-0-v2")]
    public void GetMigratedDbKey_AltSuffix_UpgradesToV2(string filename, string expected)
    {
        Assert.Equal(expected, CleanCacheTask.GetMigratedDbKey(filename, Id));
    }

    [Theory]
    [InlineData(Id + "-chromaprint-v1")]
    [InlineData(Id + "-credits-chromaprint-v1")]
    public void GetMigratedDbKey_CurrentFormat_ReturnsUnchanged(string filename)
    {
        Assert.Equal(filename, CleanCacheTask.GetMigratedDbKey(filename, Id));
    }
}
