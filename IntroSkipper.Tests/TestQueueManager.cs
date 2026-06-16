// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using IntroSkipper.Manager;
using Xunit;

public class TestQueueManager
{
    [Theory]
    // Directory prefix match (the primary use case: exclude a remote/cloud mount).
    [InlineData("/mnt/rd/Series/Season 01/S01E01.mkv", "/mnt/rd", true)]
    [InlineData("/media/local/Series/Season 01/S01E01.mkv", "/mnt/rd", false)]
    // Matching is case-insensitive in both directions.
    [InlineData("/mnt/RD/Series/S01E01.mkv", "/mnt/rd", true)]
    [InlineData("/mnt/rd/Series/S01E01.mkv", "/MNT/RD", true)]
    // A bare directory name anywhere in the path is enough.
    [InlineData("/media/zurg/Movies/Film (2020).mkv", "zurg", true)]
    [InlineData("C:\\Media\\Real-Debrid\\Show\\S01E01.mkv", "Real-Debrid", true)]
    // Windows-style separators are matched verbatim.
    [InlineData("C:\\Media\\Local\\Show\\S01E01.mkv", "\\Media\\Remote\\", false)]
    public void IsPathExcluded_SingleFragment_MatchesExpected(string path, string fragment, bool expected)
    {
        Assert.Equal(expected, QueueManager.IsPathExcluded(path, new[] { fragment }));
    }

    [Fact]
    public void IsPathExcluded_MatchesAnyConfiguredFragment()
    {
        string[] fragments = ["/mnt/rd", "/media/zurg", "Real-Debrid"];

        Assert.True(QueueManager.IsPathExcluded("/media/zurg/Movies/Film.mkv", fragments));
        Assert.True(QueueManager.IsPathExcluded("/mnt/rd/Show/S01E01.mkv", fragments));
        Assert.False(QueueManager.IsPathExcluded("/media/local/Show/S01E01.mkv", fragments));
    }

    [Fact]
    public void IsPathExcluded_EmptyPath_ReturnsFalse()
    {
        Assert.False(QueueManager.IsPathExcluded(string.Empty, new[] { "/mnt/rd" }));
    }

    [Fact]
    public void IsPathExcluded_NoFragments_ReturnsFalse()
    {
        Assert.False(QueueManager.IsPathExcluded("/mnt/rd/Show/S01E01.mkv", Array.Empty<string>()));
    }

    [Fact]
    public void IsPathExcluded_EmptyFragmentNeverMatches()
    {
        // A stray empty fragment must not cause every path to be excluded.
        Assert.False(QueueManager.IsPathExcluded("/media/local/Show/S01E01.mkv", new[] { string.Empty }));
    }
}
