// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CreditsRunner;
using IntroSkipper.Configuration;
using IntroSkipper.FFmpeg;
using Xunit;

/// <summary>
/// The offline credits runner in tools/CreditsRunner: how it scores predictions against labeled
/// credits parts, which changes between two runs it calls regressions, and one real run over a
/// fixture clip.
/// </summary>
public class TestCreditsRunner
{
    [Fact]
    public void Score_MatchesEachPartOnce_AndCountsStoryOutsidePartsAndIgnoreRanges()
    {
        // The first part's start is a band from 100 to 102, as a fade would be. The second part is
        // never predicted, and its last ten seconds are ignored, so they are not missed either.
        // The prediction at 305 lies in styled credits the label ignores.
        var label = new Label(
            "episode",
            [new LabeledPart(new Boundary(100, 102), new Boundary(120, 120)), new LabeledPart(new Boundary(200, 200), new Boundary(250, 250))],
            Ignore: [new Interval(240, 250), new Interval(300, 320)]);

        var score = Scorer.Score(label, Result(new(98, 121, "Combined"), new(150, 160, "BlackFrame"), new(305, 315, "KeyframeVisuals")));

        Assert.Equal([new PartScore(-2, 1), new PartScore(null, null)], score.Parts);
        Assert.Equal([new Candidate(150, 160, "BlackFrame")], score.FalseParts);
        Assert.Equal(2 + 1 + 10, score.StorySkipped, 6);
        Assert.Equal(40, score.CreditsMissed, 6);
    }

    [Fact]
    public void Score_OnePredictionOverTwoParts_MatchesOnlyOne()
    {
        // One candidate across both parts of split credits skips the scene between them, and
        // the second part counts as missed, although its credits are covered.
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(200, 200)), new LabeledPart(new Boundary(210, 210), new Boundary(300, 300))]);

        var score = Scorer.Score(label, Result(new Candidate(100, 300, "Combined")));

        Assert.Equal([new PartScore(0, 100), new PartScore(null, null)], score.Parts);
        Assert.Equal(10, score.StorySkipped, 6);
        Assert.Equal(0, score.CreditsMissed, 6);
    }

    [Fact]
    public void Score_SecondPredictionInsideAMatchedPart_IsNotAFalsePart()
    {
        // Credits split into two candidates the combiner kept apart: one matches the part, the
        // other covers credits too and skips no story.
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(200, 200))]);

        var score = Scorer.Score(label, Result(new(100, 140, "BlackFrame"), new(160, 200, "BlackFrame")));

        Assert.Equal([new PartScore(0, -60)], score.Parts);
        Assert.Empty(score.FalseParts);
        Assert.Equal(0, score.StorySkipped);
        Assert.Equal(20, score.CreditsMissed, 6);
    }

    [Fact]
    public void Regressions_FlagMissesNewFalsePartsNewStoryAndErrorGrowthBeyondHalfASecond()
    {
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(200, 200)), new LabeledPart(new Boundary(300, 300), new Boundary(400, 400))]);
        var baseline = Scorer.Score(label, Result(new(100, 200, "BlackFrame"), new(300, 400, "BlackFrame"), new(450, 460, "BlackFrame")));

        // The start moves 0.4 s, which is noise; the end moves 0.6 s, which is not. The false
        // part at 450 moves a second, so it is not new, but the second it now skips is.
        var candidate = Scorer.Score(label, Result(new(100.4, 199.4, "BlackFrame"), new(451, 461, "BlackFrame"), new(500, 520, "KeyframeVisuals")));

        Assert.Equal(
            ["part 1 end error +0.00 -> -0.60 s", "part 2 is now missed", "new false part 500.00-520.00 KeyframeVisuals", "21.00 s of new story skipped"],
            Differ.Regressions(baseline, candidate));
    }

    [Fact]
    public void Labels_ReadABoundaryAsATimeOrABand_AndRejectAMissingPartsList()
    {
        var label = JsonSerializer.Deserialize<Label>("""{ "id": "e", "holdout": true, "parts": [{ "start": [100, 102.5], "end": 120 }] }""", Json.Options)!;

        Assert.Equal(new LabeledPart(new Boundary(100, 102.5), new Boundary(120, 120)), Assert.Single(label.Parts));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Label>("""{ "id": "e", "parts": [{ "start": [102, 100], "end": 120 }] }""", Json.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Label>("""{ "id": "e" }""", Json.Options));
    }

    [FactSkipFFmpegTests]
    public async Task Run_OverAFixtureClip_RecordsTheCandidatesAndEveryFfmpegCall()
    {
        var file = new CorpusFile("cut-to-black", "../../../video/cut-to-black.mp4", 39);

        var results = await new Runner(new PluginConfiguration(), repeats: 2, TextWriter.Null).RunAsync([file], CancellationToken.None);

        Assert.StartsWith("ffmpeg version", results.Ffmpeg, StringComparison.Ordinal);
        var episode = Assert.Single(results.Episodes);
        Assert.Null(episode.Failure);
        Assert.False(episode.Nondeterministic);
        Assert.Equal((0, 39), (episode.WindowStart, episode.WindowEnd));
        var scan = Assert.Single(episode.Calls, call => call.Trigger == nameof(IFFmpegService.ScanKeyframesAsync));
        Assert.True(scan.Processes >= 1 && scan.WallSeconds > 0);
        if (OperatingSystem.IsLinux())
        {
            // A misread struct rusage gives zero or garbage rather than a few milliseconds of ffmpeg.
            Assert.InRange(scan.CpuSeconds!.Value, 0.001, 60);
        }
        else
        {
            Assert.Null(scan.CpuSeconds);
        }

        // The roll runs from the cut to black at 17.5 s to the end of the clip. The raw candidates
        // end at the last keyframe, 36 s; only the combiner extends the end to the window end,
        // so an exact end shows the scorer reads the combined candidates.
        var label = new Label("cut-to-black", [new LabeledPart(new Boundary(17.5, 17.5), new Boundary(39, 39))]);
        Assert.Equal(0, Assert.Single(Scorer.Score(label, episode).Parts).EndError);

        var path = Path.Combine(Path.GetTempPath(), $"credits-runner-{Guid.NewGuid():N}.json");
        try
        {
            Json.Write(path, results);
            Assert.Equal(episode.Combined, Assert.Single(Json.Read<RunResults>(path).Episodes).Combined);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static EpisodeResult Result(params Candidate[] combined)
        => new("episode", 0, 450, combined, combined, null, false, 0, null, 0, []);
}
