// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using IntroSkipper.ScheduledTasks;
using Xunit;

public sealed class TestCleanCacheTask
{
    private const string Id = "aabbccdd00112233aabbccdd00112233";

    [Theory]
    // Text-format (very old) fingerprint files — no usable binary data.
    [InlineData(Id)]
    [InlineData(Id + "-credits")]
    // Stale-format detection files — version bump means format may have changed; delete and repopulate.
    [InlineData(Id + "-blackframes-0-v1")]
    [InlineData(Id + "-blackframes-0-credits-v1")]
    [InlineData(Id + "-silence-0-v2")]
    [InlineData(Id + "-keyframes-0-v1")]
    [InlineData(Id + "-credits-blackframes-90-alt")]
    [InlineData(Id + "-blackframes-0-alt")]
    public void GetMigratedDbKey_NonMigratable_ReturnsNull(string filename)
    {
        Assert.Null(CleanCacheTask.GetMigratedDbKey(filename, Id));
    }

    [Theory]
    // Current-format files can be raw-copied into the DB as-is.
    [InlineData(Id + "-chromaprint-v1")]
    [InlineData(Id + "-credits-chromaprint-v1")]
    [InlineData(Id + "-blackframes-1-v2")]
    [InlineData(Id + "-silence-1-v3")]
    [InlineData(Id + "-keyframes-1-v2")]
    public void GetMigratedDbKey_CurrentFormat_ReturnsUnchanged(string filename)
    {
        Assert.Equal(filename, CleanCacheTask.GetMigratedDbKey(filename, Id));
    }
}
