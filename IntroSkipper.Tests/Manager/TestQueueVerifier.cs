// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using Xunit;

public sealed class TestQueueVerifier
{
    private static readonly PluginConfiguration OtherPluginWideSettings = new() { FullLengthChapters = true };

    [Theory]
    [InlineData(true, EpisodeState.Analyzed)]
    [InlineData(false, EpisodeState.NoSegments)]
    public void Keep_SettlesStaleRecordWithCurrentKeepHash(bool hasSegment, EpisodeState expected)
    {
        var season = new Season();
        var run = season.Verify(Keep(), AnalysisMode.Introduction, season.Record(OtherPluginWideSettings, AnalysisMode.Introduction), hasSegment);

        Assert.Equal(expected, run.Episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Empty(run.Verifier.KeepConfirmations);
    }

    [Fact]
    public void KeepOff_ReopensTheStaleRecord()
    {
        var season = new Season();
        var run = season.Verify(new PluginConfiguration(), AnalysisMode.Introduction, season.Record(OtherPluginWideSettings, AnalysisMode.Introduction));

        Assert.Equal(EpisodeState.NotAnalyzed, run.Episode.GetAnalyzed(AnalysisMode.Introduction));
    }

    [Theory]
    [InlineData(AnalyzerAction.Chapter)]
    [InlineData(AnalyzerAction.None)]
    public void Keep_ReopensWhenTheSeasonActionChanged(AnalyzerAction action)
    {
        var record = new Season().Record(OtherPluginWideSettings, AnalysisMode.Introduction);
        var run = new Season(Action: action).Verify(Keep(), AnalysisMode.Introduction, record);

        Assert.Equal(EpisodeState.NotAnalyzed, run.Episode.GetAnalyzed(AnalysisMode.Introduction));
    }

    [Theory]
    [InlineData(30, null, null, null, null)]
    [InlineData(null, 12, null, null, null)]
    [InlineData(null, null, true, null, null)]
    [InlineData(null, null, null, false, null)]
    [InlineData(null, null, null, null, true)]
    public void Keep_ReopensWhenASeasonOverrideChanged(int? percent, int? limit, bool? derive, bool? subtitleRecap, bool? subtitlePreview)
    {
        var record = new Season().Record(OtherPluginWideSettings, AnalysisMode.Recap);
        var edited = new Season(Percent: percent, Limit: limit, Derive: derive, SubtitleRecap: subtitleRecap, SubtitlePreview: subtitlePreview);
        var run = edited.Verify(Keep(), AnalysisMode.Recap, record);

        Assert.Equal(EpisodeState.NotAnalyzed, run.Episode.GetAnalyzed(AnalysisMode.Recap));
    }

    [Fact]
    public void Keep_SeasonSubtitleToggleRerunsSubtitlesAlone()
    {
        var record = new Season().Record(Keep(), AnalysisMode.Recap);
        var run = new Season(SubtitleRecap: true).Verify(Keep(), AnalysisMode.Recap, record);

        Assert.Equal(EpisodeState.NotAnalyzed, run.Episode.GetAnalyzed(AnalysisMode.Recap));
        Assert.True(run.Episode.NeedsSubtitleOnlyReanalysis(AnalysisMode.Recap));
    }

    [Fact]
    public void Keep_ReopensResultMadeWithoutChromaprintOnceItIsAvailable()
    {
        var record = new Season(FfmpegValid: false).Record(OtherPluginWideSettings, AnalysisMode.Introduction);
        var run = new Season().Verify(Keep(), AnalysisMode.Introduction, record);

        Assert.Equal(EpisodeState.NotAnalyzed, run.Episode.GetAnalyzed(AnalysisMode.Introduction));
    }

    [Fact]
    public void Keep_FailedProbeKeepsResultMadeWithChromaprint()
    {
        var record = new Season().Record(OtherPluginWideSettings, AnalysisMode.Introduction);
        var run = new Season(FfmpegValid: false).Verify(Keep(), AnalysisMode.Introduction, record);

        Assert.Equal(EpisodeState.Analyzed, run.Episode.GetAnalyzed(AnalysisMode.Introduction));
    }

    [Fact]
    public void Keep_RecheckRequestSendsStaleRecordBack()
    {
        var record = new Season().Record(OtherPluginWideSettings, AnalysisMode.Introduction);
        var run = new Season(Request: "r1").Verify(Keep(), AnalysisMode.Introduction, record);

        Assert.Equal(EpisodeState.NotAnalyzed, run.Episode.GetAnalyzed(AnalysisMode.Introduction));
    }

    [Fact]
    public void Recheck_ConfirmsCurrentRecordWithoutReopening()
    {
        var config = new PluginConfiguration();
        var record = new Season().Record(config, AnalysisMode.Introduction);
        var rechecked = new Season(Request: "r1");
        var run = rechecked.Verify(config, AnalysisMode.Introduction, record);

        Assert.Equal(EpisodeState.Analyzed, run.Episode.GetAnalyzed(AnalysisMode.Introduction));
        var confirmation = Assert.Single(run.Verifier.KeepConfirmations);
        Assert.Equal(AnalysisMode.Introduction, confirmation.Mode);
        Assert.Equal(rechecked.Hashes(config, AnalysisMode.Introduction), confirmation.Hashes);
        Assert.Equal([run.Episode.EpisodeId], confirmation.ItemIds);
    }

    [Fact]
    public void Confirmation_UsesTheAvailablePairWhileTheProbeFails()
    {
        var config = Keep();
        var record = new Season().Record(config, AnalysisMode.Introduction);
        var run = new Season(Request: "r1", FfmpegValid: false).Verify(config, AnalysisMode.Introduction, record);

        Assert.Equal(EpisodeState.Analyzed, run.Episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Equal(new Season(Request: "r1").Hashes(config, AnalysisMode.Introduction), Assert.Single(run.Verifier.KeepConfirmations).Hashes);
    }

    [Fact]
    public void Confirmation_SkipsKeptOtherSourceAndUserProvidedRecords()
    {
        var season = new Season();
        var kept = season.Verify(Keep(), AnalysisMode.Introduction, season.Record(OtherPluginWideSettings, AnalysisMode.Introduction));
        var otherSource = season.Verify(
            new PluginConfiguration(),
            AnalysisMode.Introduction,
            season.Record(new PluginConfiguration { UseEmbeddedChaptersForAnalysis = true }, AnalysisMode.Introduction) with { KeepHash = "old" });
        var userProvided = season.Verify(
            new PluginConfiguration(),
            AnalysisMode.Introduction,
            season.Record(new PluginConfiguration(), AnalysisMode.Introduction) with { KeepHash = "old" },
            userProvided: true);

        Assert.Equal(EpisodeState.Analyzed, kept.Episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Equal(EpisodeState.Analyzed, otherSource.Episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Equal(EpisodeState.UserProvided, userProvided.Episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Empty(kept.Verifier.KeepConfirmations);
        Assert.Empty(otherSource.Verifier.KeepConfirmations);
        Assert.Empty(userProvided.Verifier.KeepConfirmations);
    }

    [Fact]
    public void Keep_KeepsRecordWithoutKeepHashUnderTheAvailableKeepHash()
    {
        var record = new Season().Record(OtherPluginWideSettings, AnalysisMode.Introduction) with { KeepHash = null };
        var run = new Season(FfmpegValid: false).Verify(Keep(), AnalysisMode.Introduction, record);

        Assert.Equal(EpisodeState.Analyzed, run.Episode.GetAnalyzed(AnalysisMode.Introduction));
        var confirmation = Assert.Single(run.Verifier.KeepConfirmations);
        Assert.Equal(new RecordHashes(record.ConfigHash, new Season().Hashes(Keep(), AnalysisMode.Introduction).KeepHash), confirmation.Hashes);
    }

    [Fact]
    public void KeepOff_RecordWithoutKeepHashIsJudgedByConfigHash()
    {
        var season = new Season();
        var run = season.Verify(new PluginConfiguration(), AnalysisMode.Introduction, season.Record(OtherPluginWideSettings, AnalysisMode.Introduction) with { KeepHash = null });

        Assert.Equal(EpisodeState.NotAnalyzed, run.Episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Empty(run.Verifier.KeepConfirmations);
    }

    [Fact]
    public void Keep_ReplacedMediaResetsRecapOnlyItem()
    {
        var season = new Season();
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), FileVersion = 2 };
        var records = new Dictionary<AnalysisMode, AnalysisRecord>
        {
            [AnalysisMode.Recap] = season.Record(OtherPluginWideSettings, AnalysisMode.Recap) with { FileVersion = 1 },
            [AnalysisMode.Preview] = season.Record(OtherPluginWideSettings, AnalysisMode.Preview) with { FileVersion = 1 },
        };
        season.Verify(Keep(), [AnalysisMode.Recap, AnalysisMode.Preview], episode, records);

        Assert.True(episode.FileChanged);
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Recap));
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    [Fact]
    public void Keep_SidecarChangeReopensKeptRecapForItsFullChain()
    {
        var config = Keep();
        config.EnableSubtitleRecapDetection = true;
        var drifted = new PluginConfiguration { EnableSubtitleRecapDetection = true, FullLengthChapters = true };
        var season = new Season();
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), FileVersion = 5, SubtitleFileVersion = 11 };
        var records = new Dictionary<AnalysisMode, AnalysisRecord>
        {
            [AnalysisMode.Recap] = season.Record(drifted, AnalysisMode.Recap) with { FileVersion = 10 },
            [AnalysisMode.Introduction] = season.Record(config, AnalysisMode.Introduction) with { FileVersion = 5 },
        };
        season.Verify(config, [AnalysisMode.Recap], episode, records);

        Assert.False(episode.FileChanged);
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Recap));
        Assert.False(episode.NeedsSubtitleOnlyReanalysis(AnalysisMode.Recap));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChapterSourceSwitch_ChecksOnlyWithKeepOff(bool keep)
    {
        var season = new Season();
        var record = season.Record(new PluginConfiguration { UseEmbeddedChaptersForAnalysis = true }, AnalysisMode.Introduction);
        var run = season.Verify(new PluginConfiguration { KeepExistingAnalysis = keep }, AnalysisMode.Introduction, record);

        Assert.Equal(EpisodeState.Analyzed, run.Episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Equal(!keep, run.Episode.ChapterSourceChecks.ContainsKey(AnalysisMode.Introduction));
    }

    private static PluginConfiguration Keep() => new() { KeepExistingAnalysis = true };

    private sealed record Season(
        AnalyzerAction Action = AnalyzerAction.Default,
        int? Percent = null,
        int? Limit = null,
        bool? Derive = null,
        bool? SubtitleRecap = null,
        bool? SubtitlePreview = null,
        string Request = "",
        bool FfmpegValid = true)
    {
        public SeasonHashInputs Inputs => new(Request, Percent, Limit, Derive, SubtitleRecap, SubtitlePreview);

        public RecordHashes Hashes(PluginConfiguration config, AnalysisMode mode)
            => ConfigHasher.Record(config, mode, Action, FfmpegValid, Inputs);

        public AnalysisRecord Record(PluginConfiguration config, AnalysisMode mode)
        {
            var hashes = Hashes(config, mode);
            return new AnalysisRecord(hashes.ConfigHash, null, hashes.KeepHash);
        }

        public (QueuedEpisode Episode, QueueVerifier Verifier) Verify(PluginConfiguration config, AnalysisMode mode, AnalysisRecord record, bool hasSegment = true, bool userProvided = false)
        {
            var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
            var verifier = Verify(config, [mode], episode, new Dictionary<AnalysisMode, AnalysisRecord> { [mode] = record }, hasSegment || userProvided, userProvided);
            return (episode, verifier);
        }

        public QueueVerifier Verify(
            PluginConfiguration config,
            AnalysisMode[] modes,
            QueuedEpisode episode,
            IReadOnlyDictionary<AnalysisMode, AnalysisRecord> records,
            bool hasSegment = true,
            bool userProvided = false)
        {
            var snapshot = new SeasonQueueSnapshot(
                records.ToDictionary(entry => (episode.EpisodeId, entry.Key), entry => entry.Value),
                Enum.GetValues<AnalysisMode>().ToDictionary(mode => mode, _ => Action),
                hasSegment ? new Dictionary<Guid, IReadOnlySet<AnalysisMode>> { [episode.EpisodeId] = modes.ToHashSet() } : [],
                userProvided ? modes.ToDictionary(mode => mode, IReadOnlySet<Guid> (_) => new HashSet<Guid> { episode.EpisodeId }) : []);
            episode.SubtitleRecapDetectionOverride = SubtitleRecap;
            episode.SubtitlePreviewDetectionOverride = SubtitlePreview;
            var verifier = new QueueVerifier(config, modes, snapshot, FfmpegValid, Inputs);
            verifier.Classify(episode);
            return verifier;
        }
    }
}
