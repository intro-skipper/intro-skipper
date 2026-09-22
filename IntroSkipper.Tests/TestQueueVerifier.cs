// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using Xunit;

public sealed class TestQueueVerifier
{
    [Fact]
    public void Classify_ReopensShortcutWhenItsResolvedTargetChanges()
    {
        var itemId = Guid.NewGuid();
        var snapshot = new SeasonQueueSnapshot(
            new Dictionary<(Guid, AnalysisMode), AnalysisRecord>
            {
                [(itemId, AnalysisMode.Introduction)] = new AnalysisRecord("hash", 1, "/remote/old.mkv", 120),
            },
            new Dictionary<AnalysisMode, AnalyzerAction>(),
            new Dictionary<Guid, IReadOnlySet<AnalysisMode>>(),
            new Dictionary<AnalysisMode, IReadOnlySet<Guid>>());
        var candidate = new QueuedEpisode
        {
            EpisodeId = itemId,
            IsShortcut = true,
            ShortcutPath = "/remote/new.mkv",
            FileVersion = 1,
        };

        new QueueVerifier(new PluginConfiguration(), [AnalysisMode.Introduction], snapshot, ffmpegValid: false).Classify(candidate);

        Assert.True(candidate.FileChanged);
        Assert.Equal(EpisodeState.NotAnalyzed, candidate.GetAnalyzed(AnalysisMode.Introduction));
    }
}
