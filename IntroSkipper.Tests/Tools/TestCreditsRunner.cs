// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CreditsRunner;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Xunit;

/// <summary>
/// The offline credits runner in tools/CreditsRunner: how it scores predictions against labeled
/// credits parts, which changes between two runs it calls regressions, one real run over a
/// fixture clip, and intro-mode runs over a pair of fixture clips.
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

        var score = Scorer.Score(label, Result("episode", new(98, 121, "Combined"), new(150, 160, "BlackFrame"), new(305, 315, "KeyframeVisuals")));

        Assert.Equal([new PartScore(-2, 1), new PartScore(null, null)], score.Parts);
        Assert.Equal([new Candidate(150, 160, "BlackFrame")], score.FalseParts);
        Assert.Equal(2 + 1 + 10, score.StorySkipped, 6);
        Assert.Equal(40, score.CreditsMissed, 6);
    }

    [Fact]
    public void Score_OnePredictionOverTwoParts_MatchesOnlyOne()
    {
        // One candidate across both parts of split credits skips the scene between them, and
        // the second part counts as missed, although its credits are covered. The first part's
        // end error is the scene alone: the second part's credits are not story.
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(200, 200)), new LabeledPart(new Boundary(210, 210), new Boundary(300, 300))]);

        var score = Scorer.Score(label, Result("episode", new Candidate(100, 300, "Combined")));

        Assert.Equal([new PartScore(0, 10), new PartScore(null, null)], score.Parts);
        Assert.Equal(10, score.StorySkipped, 6);
        Assert.Equal(0, score.CreditsMissed, 6);
    }

    [Fact]
    public void Score_StartInsideAnIgnoreRangeThatMeetsThePart_ScoresZero()
    {
        // Styled credits run straight into a short card part, and the prediction starts partway
        // into them: it skips no story and misses no required credits.
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(106, 106))], Ignore: [new Interval(86, 100)]);

        var score = Scorer.Score(label, Result("episode", new Candidate(90, 106, "KeyframeVisuals")));

        Assert.Equal([new PartScore(0, 0)], score.Parts);
        Assert.Equal(0, score.StorySkipped);
    }

    [Fact]
    public void Score_EarlyStartThroughAnIgnoreRange_CountsOnlyTheStory()
    {
        // 5 s of story, an ED the label ignores, then a 6 s next-episode card before the part:
        // the start costs the 11 s of story and nothing for the ED.
        var label = new Label("episode", [new LabeledPart(new Boundary(106, 106), new Boundary(166, 166))], Ignore: [new Interval(10, 100)]);

        var score = Scorer.Score(label, Result("episode", new Candidate(5, 166, "Combined")));

        Assert.Equal([new PartScore(-11, 0)], score.Parts);
        Assert.Equal(11, score.StorySkipped, 6);
    }

    [Fact]
    public void Score_LateStart_CountsOnlyTheRequiredCreditsMissed()
    {
        // The start band ends at 102 and the prediction starts at 170, past 10 s the label
        // ignores inside the part, so 58 s of required credits are missed.
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 102), new Boundary(200, 200))], Ignore: [new Interval(150, 160)]);

        var score = Scorer.Score(label, Result("episode", new Candidate(170, 200, "BlackFrame")));

        Assert.Equal([new PartScore(58, 0)], score.Parts);
        Assert.Equal(58, score.CreditsMissed, 6);
    }

    [Fact]
    public void Score_PredictionOverOnlyABandAndIgnoredTitles_MatchesNoPart()
    {
        // A card part, styled titles the label ignores, then a roll. The prediction covers the
        // titles and the last 0.1 s of the card's end band, which holds no required credit.
        var label = new Label("episode", CardTitlesRoll(new Boundary(106.8, 107)), Ignore: [new Interval(107, 227)]);

        var score = Scorer.Score(label, Result("episode", new Candidate(106.9, 226.9, "Chromaprint")));

        Assert.Equal([new PartScore(null, null), new PartScore(null, null)], score.Parts);
        Assert.Empty(score.FalseParts);
    }

    [Fact]
    public void Score_OnePredictionAcrossCardTitlesAndRoll_MatchesBothAndCountsOuterStoryOnce()
    {
        // The ignored titles join the card and the roll into one block, so one prediction can
        // match both. Story before the block counts on the card and story after it on the roll.
        var label = new Label("episode", CardTitlesRoll(new Boundary(106.5, 107)), Ignore: [new Interval(106.5, 227)]);

        Assert.Equal(
            [new PartScore(0, 0), new PartScore(0, 0)],
            Scorer.Score(label, Result("episode", new Candidate(100, 327, "Combined"))).Parts);
        Assert.Equal(
            [new PartScore(-10, 0), new PartScore(0, 3)],
            Scorer.Score(label, Result("episode", new Candidate(90, 330, "Combined"))).Parts);
    }

    [Fact]
    public void Regressions_FlagACardLostFromAPredictionAcrossTheBlock()
    {
        // The prediction now starts inside the ignored titles: the roll still scores 0/0, and
        // the card is missed.
        var label = new Label("episode", CardTitlesRoll(new Boundary(106.5, 107)), Ignore: [new Interval(106.5, 227)]);
        var baseline = Scorer.Score(label, Result("episode", new Candidate(100, 327, "Combined")));

        var candidate = Scorer.Score(label, Result("episode", new Candidate(106.8, 327, "Combined")));

        Assert.Equal([new PartScore(null, null), new PartScore(0, 0)], candidate.Parts);
        Assert.Equal(["part 1 is now missed"], Differ.Regressions(baseline, candidate));
    }

    [Fact]
    public void Score_SecondPredictionInsideAMatchedPart_IsNotAFalsePart()
    {
        // Credits split into two candidates the combiner kept apart: one matches the part, the
        // other covers credits too and skips no story.
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(200, 200))]);

        var score = Scorer.Score(label, Result("episode", new(100, 140, "BlackFrame"), new(160, 200, "BlackFrame")));

        Assert.Equal([new PartScore(0, -60)], score.Parts);
        Assert.Empty(score.FalseParts);
        Assert.Equal(0, score.StorySkipped);
        Assert.Equal(20, score.CreditsMissed, 6);
    }

    [Fact]
    public void Score_MatchesByRequiredCredits_NotByIgnoredTimeInsideThePart()
    {
        // The part's first 70 s are ignored. The first prediction covers 65 s of them and the
        // second 25 s of required credits, so the second matches.
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(200, 200))], Ignore: [new Interval(100, 170)]);

        var score = Scorer.Score(label, Result("episode", new(100, 165, "Chromaprint"), new(175, 200, "BlackFrame")));

        Assert.Equal([new PartScore(5, 0)], score.Parts);
    }

    [Fact]
    public void Score_EarlyStartThroughAnotherPart_CountsOnlyTheStory()
    {
        // The prediction starts inside a card that is a block of its own and runs through the
        // roll: the roll's start costs the 90 s of story after the card, not the card's 5 s.
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(110, 110)), new LabeledPart(new Boundary(200, 200), new Boundary(300, 300))]);

        var score = Scorer.Score(label, Result("episode", new Candidate(105, 300, "Combined")));

        Assert.Equal([new PartScore(null, null), new PartScore(-90, 0)], score.Parts);
    }

    [Fact]
    public void Score_EarlyEnd_CountsOnlyTheRequiredCreditsMissed()
    {
        // The prediction stops 60 s early, 10 s of which the label ignores.
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(200, 200))], Ignore: [new Interval(150, 160)]);

        var score = Scorer.Score(label, Result("episode", new Candidate(100, 140, "BlackFrame")));

        Assert.Equal([new PartScore(0, -50)], score.Parts);
    }

    [Fact]
    public void Score_PartsWithAtMostHalfASecondOfStoryBetween_FormOneBlock()
    {
        // One prediction over both parts. With 0.4 s of story between them it matches both, and
        // that story counts on each; with 0.6 s the second part is a block of its own and missed.
        static PartScore[] Score(double gap) => [.. Scorer.Score(
            new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(200, 200)), new LabeledPart(new Boundary(200 + gap, 200 + gap), new Boundary(300, 300))]),
            Result("episode", new Candidate(100, 300, "Combined"))).Parts];

        var block = Score(0.4);
        Assert.Equal((0, 0.4, -0.4, 0), (block[0].StartError!.Value, Math.Round(block[0].EndError!.Value, 6), Math.Round(block[1].StartError!.Value, 6), block[1].EndError!.Value));

        var apart = Score(0.6);
        Assert.Equal(0.6, apart[0].EndError!.Value, 6);
        Assert.True(apart[1].Missed);
    }

    [Fact]
    public void Regressions_FlagMissesNewFalsePartsNewStoryAndErrorGrowthBeyondHalfASecond()
    {
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(200, 200)), new LabeledPart(new Boundary(300, 300), new Boundary(400, 400))]);
        var baseline = Scorer.Score(label, Result("episode", new(100, 200, "BlackFrame"), new(300, 400, "BlackFrame"), new(450, 460, "BlackFrame")));

        // The start moves 0.4 s, which is noise; the end moves 0.6 s, which is not. The false
        // part at 450 moves a second, so it is not new, but the second it now skips is.
        var candidate = Scorer.Score(label, Result("episode", new(100.4, 199.4, "BlackFrame"), new(451, 461, "BlackFrame"), new(500, 520, "KeyframeVisuals")));

        Assert.Equal(
            ["part 1 end error +0.00 -> -0.60 s", "part 2 is now missed", "new false part 500.00-520.00 KeyframeVisuals", "21.00 s of new story skipped"],
            Differ.Regressions(baseline, candidate));
    }

    [Fact]
    public void Diff_TagsAndCountsHoldoutRegressionsApart()
    {
        using var directory = new TempDirectory();
        Json.Write(directory.Join("baseline.json"), Run(Result("tune", new Candidate(100, 200, "BlackFrame")), Result("held", new(100, 200, "BlackFrame"), new(300, 400, "BlackFrame"))));
        Json.Write(directory.Join("candidate.json"), Run(Result("tune"), Result("held")));
        Json.Write(directory.Join("tuning-unchanged.json"), Run(Result("tune"), Result("held", new(100, 200, "BlackFrame"), new(300, 400, "BlackFrame"))));
        File.WriteAllText(directory.Join("labels.json"), """
            { "files": [
              { "id": "tune", "parts": [{ "start": 100, "end": 200 }] },
              { "id": "held", "holdout": true, "parts": [{ "start": 100, "end": 200 }, { "start": 300, "end": 400 }] }
            ] }
            """);
        using var output = new StringWriter();

        var exitCode = Commands.Diff(["--baseline", directory.Join("baseline.json"), "--candidate", directory.Join("candidate.json"), "--labels", directory.Join("labels.json")], output);

        var lines = output.ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, exitCode);
        Assert.Equal(["REGRESSION held (holdout): part 1 is now missed", "REGRESSION held (holdout): part 2 is now missed"], lines.Where(line => line.Contains("REGRESSION held", StringComparison.Ordinal)));
        Assert.Equal("REGRESSION tune: part 1 is now missed", Assert.Single(lines, line => line.Contains("REGRESSION tune", StringComparison.Ordinal)));
        Assert.StartsWith("1 tuning and 2 holdout regressions", lines[^1], StringComparison.Ordinal);

        // A holdout regression alone still fails the diff.
        Assert.Equal(1, Commands.Diff(["--baseline", directory.Join("tuning-unchanged.json"), "--candidate", directory.Join("candidate.json"), "--labels", directory.Join("labels.json")], TextWriter.Null));
    }

    [Fact]
    public async Task Recorder_CountsARunKilledAtItsTimeout()
    {
        var recorder = new CallRecorder();
        var mark = recorder.Begin();
        var (path, args) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" })
            : ("/bin/sh", new[] { "-c", "sleep 30" });

        await Assert.ThrowsAsync<TimeoutException>(() => new FFmpegProcessRunner(recorder).RunAsync(path, args, timeout: 500));

        Assert.Equal(1, recorder.Measure("x", mark).TimedOut);
    }

    [Fact]
    public void Summarize_KeepsATimeoutFromAnyTimedPass()
    {
        var file = new CorpusFile("episode", "episode.mkv", 450);
        Runner.Pass Pass(params CallTiming[] calls) => new(new QueuedEpisode(), "ffmpeg", [], [], null, new CallTiming("total", 0, 1, null), 0, calls);
        var scan = new CallTiming(nameof(IFFmpegService.ScanKeyframesAsync), 1, 2, null);
        var black = new CallTiming(nameof(IFFmpegService.DetectBlackIntervalsAsync), 1, 1, null);
        var killed = black with { WallSeconds = 300, TimedOut = 1 };
        var cut = new CallTiming(nameof(IFFmpegService.DecodeCutWindowAsync), 1, 1, null);

        // The second pass's probe timed out and the analyzer made the same calls after it.
        var same = Runner.Summarize(file, [Pass(scan, black, cut), Pass(scan, killed, cut)]);
        Assert.Equal(1, same.Calls.Sum(call => call.TimedOut));

        // The second pass's probe timed out and the analyzer took another path.
        var differ = Runner.Summarize(file, [Pass(scan, black, cut), Pass(scan, killed)]);
        Assert.Equal(1, differ.Calls.Sum(call => call.TimedOut));
        Assert.True(differ.Nondeterministic);
    }

    [Fact]
    public void Score_ListsTimedOutCalls()
    {
        using var directory = new TempDirectory();
        Json.Write(directory.Join("run.json"), Run(Unfinished("episode")));
        File.WriteAllText(directory.Join("labels.json"), """{ "files": [{ "id": "episode", "parts": [] }] }""");
        using var output = new StringWriter();

        Commands.Score(["--results", directory.Join("run.json"), "--labels", directory.Join("labels.json")], output);

        var lines = output.ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("unfinished: episode: DetectBlackIntervalsAsync, 2 timed out", Assert.Single(lines, line => line.StartsWith("unfinished:", StringComparison.Ordinal)));
        Assert.Contains("timed out: 2", lines);
    }

    [Fact]
    public void Diff_WarnsAndFailsWhenEitherRunHasATimedOutCall()
    {
        using var directory = new TempDirectory();
        Json.Write(directory.Join("clean.json"), Run(Result("episode")));
        Json.Write(directory.Join("unfinished.json"), Run(Unfinished("episode")));
        File.WriteAllText(directory.Join("labels.json"), """{ "files": [{ "id": "episode", "parts": [] }] }""");
        (int ExitCode, string[] Lines) Diff(string baseline, string candidate)
        {
            using var output = new StringWriter();
            var exitCode = Commands.Diff(["--baseline", directory.Join(baseline), "--candidate", directory.Join(candidate), "--labels", directory.Join("labels.json")], output);
            return (exitCode, output.ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        // The runs agree on every candidate, so the timeout alone fails the diff.
        Assert.Equal(0, Diff("clean.json", "clean.json").ExitCode);
        var (exitCode, lines) = Diff("clean.json", "unfinished.json");
        Assert.Equal(1, exitCode);
        Assert.Contains("warning: episode: ffmpeg timed out in the candidate; its scores are not a comparison", lines);
        (exitCode, lines) = Diff("unfinished.json", "clean.json");
        Assert.Equal(1, exitCode);
        Assert.Contains("warning: episode: ffmpeg timed out in the baseline; its scores are not a comparison", lines);
    }

    [Fact]
    public void Options_RejectARepeatedOption()
        => Assert.Throws<ArgumentException>(() => Options.Parse(["--labels", "a.json", "--labels", "b.json"], ["labels"]));

    [Fact]
    public void Options_BareFlag_IsReadAndUnknownFlagRejected()
    {
        // The flag sits between two pairs, so a parser that read it as a pair would find
        // --out where its value should be and refuse it.
        var options = Options.Parse(["--manifest", "m.json", "--no-chapters", "--out", "out.json"], ["manifest", "out"], ["no-chapters"]);

        Assert.True(options.Flag("no-chapters"));
        Assert.Equal("out.json", options.Required("out"));
        Assert.False(Options.Parse(["--manifest", "m.json"], ["manifest"], ["no-chapters"]).Flag("no-chapters"));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--bogus"], ["manifest"], ["no-chapters"]));
    }

    [Fact]
    public void Options_RejectAPairWhoseValueIsTheNextOption()
    {
        // A parser that took --no-chapters as the value of --out would write the results to a
        // file of that name and keep the chapters.
        Assert.Throws<ArgumentException>(() => Options.Parse(["--out", "--no-chapters"], ["out"], ["no-chapters"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--out"], ["out"]));
    }

    // Each refusal comes before the manifest is read, so none is needed.
    [Theory]
    [InlineData("--repeat", "--mode", "intro", "--repeat", "1")]
    [InlineData("--repeat", "--mode", "credits-pass", "--repeat", "1")]
    [InlineData("--no-chapters", "--no-chapters")]
    [InlineData("--minimum-credits-duration", "--mode", "intro", "--minimum-credits-duration", "10")]
    [InlineData("--mode", "--mode", "season")]
    public async Task Run_RefusesAnOptionTheModeDoesNotTake(string named, params string[] options)
    {
        var refusal = await Assert.ThrowsAsync<ArgumentException>(() => Commands.RunAsync(["--manifest", "m.json", "--out", "out.json", .. options], TextWriter.Null, CancellationToken.None));
        Assert.Contains(named, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Results_WrittenBeforeModeAndNoChapters_ReadAsCreditsWithChapters()
    {
        using var directory = new TempDirectory();
        var path = directory.Join("old.json");
        var configuration = JsonSerializer.SerializeToElement(new PluginConfiguration(), Json.Options);
        var json = JsonSerializer.SerializeToNode(new RunResults("commit", "ffmpeg", "cpu", "os", 1, configuration, [Result("e")], RunMode.Introduction, NoChapters: true), Json.Options)!.AsObject();
        Assert.True(json.Remove("mode") && json.Remove("noChapters"));
        File.WriteAllText(path, json.ToJsonString());

        var results = Json.Read<RunResults>(path);

        Assert.Equal((RunMode.Credits, false), (results.Mode, results.NoChapters));
    }

    [Fact]
    public void Diff_RefusesRunsOfDifferentModes()
    {
        using var directory = new TempDirectory();
        var configuration = JsonSerializer.SerializeToElement(new PluginConfiguration(), Json.Options);
        Json.Write(directory.Join("credits.json"), new RunResults("commit", "ffmpeg", "cpu", "os", 1, configuration, [Result("e")]));
        Json.Write(directory.Join("intro.json"), new RunResults("commit", "ffmpeg", "cpu", "os", 1, configuration, [Result("e")], RunMode.Introduction));
        File.WriteAllText(directory.Join("labels.json"), """{ "files": [{ "id": "e", "parts": [] }] }""");

        Assert.Throws<ArgumentException>(() => Commands.Diff(["--baseline", directory.Join("credits.json"), "--candidate", directory.Join("intro.json"), "--labels", directory.Join("labels.json")], TextWriter.Null));
    }

    [Fact]
    public async Task Run_IntroMode_RefusesAMovie()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(directory.Join("manifest.json"), """{ "files": [{ "id": "m", "path": "m.mkv", "duration": 6000, "movie": true }] }""", CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<ArgumentException>(() => Commands.RunAsync(["--manifest", directory.Join("manifest.json"), "--out", directory.Join("out.json"), "--mode", "intro"], TextWriter.Null, CancellationToken.None));
        Assert.Contains("movies: m", refusal.Message, StringComparison.Ordinal);
    }

    [FactSkipFFmpegTests]
    public async Task Run_IntroMode_ComparesTheFilesOfOneSeason()
    {
        // The two clips share their first 17 s of audio; only a season pass compares them.
        var results = await RunSeasonAsync(RunMode.Introduction, "s1", "s1");
        var apart = await RunSeasonAsync(RunMode.Introduction, null, null);

        Assert.All(results.Episodes, episode =>
        {
            Assert.Null(episode.Failure);
            var intro = Assert.Single(episode.Combined);
            Assert.Equal(("Chromaprint", 0.0), (intro.Source, intro.Start));
        });
        Assert.All(apart.Episodes, episode => Assert.Empty(episode.Combined));
    }

    [FactSkipFFmpegTests]
    public async Task Run_CreditsPassMode_ComparesTheFilesOfOneSeason()
    {
        // Audio only, so the keyframe analyzer finds nothing and Chromaprint is the only source.
        var results = await RunSeasonAsync(RunMode.CreditsPass, "s1", "s1");
        var apart = await RunSeasonAsync(RunMode.CreditsPass, null, null);

        Assert.Equal(RunMode.CreditsPass, results.Mode);
        Assert.All(results.Episodes, episode =>
        {
            Assert.Null(episode.Failure);
            Assert.NotEmpty(episode.Combined);
        });
        Assert.All(apart.Episodes, episode => Assert.Empty(episode.Combined));
    }

    [FactSkipFFmpegTests]
    public async Task Run_IntroMode_AttributesEachCallToItsEpisode()
    {
        var results = await RunSeasonAsync(RunMode.Introduction, "s1", "s1");

        Assert.All(results.Episodes, episode => Assert.Single(episode.Calls, call => call.Trigger == nameof(IFFmpegService.FingerprintAsync)));
    }

    [Fact]
    public void Labels_ReadABoundaryAsATimeOrABand_AndRejectAMissingPartsList()
    {
        var label = JsonSerializer.Deserialize<Label>("""{ "id": "e", "holdout": true, "parts": [{ "start": [100, 102.5], "end": 120 }] }""", Json.Options)!;

        Assert.Equal(new LabeledPart(new Boundary(100, 102.5), new Boundary(120, 120)), Assert.Single(label.Parts));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Label>("""{ "id": "e", "parts": [{ "start": [102, 100], "end": 120 }] }""", Json.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Label>("""{ "id": "e" }""", Json.Options));
    }

    // The scorer walks parts by label position, and a part with no required credits can never match.
    [Theory]
    [InlineData("""[{ "start": 300, "end": 400 }, { "start": 100, "end": 200 }]""", "[]", "e: part 2 starts at 100 before part 1 ends at 400")]
    [InlineData("""[{ "start": 50, "end": [100, 110] }, { "start": 105, "end": 200 }]""", "[]", "e: part 2 starts at 105 before part 1 ends at 110")]
    [InlineData("""[{ "start": [100, 101], "end": [101, 102] }]""", "[]", "e: part 1 has no required credits")]
    [InlineData("""[{ "start": 50, "end": 60 }, { "start": [100, 102], "end": [101, 103] }]""", "[]", "e: part 2 has no required credits")]
    [InlineData("""[{ "start": 100, "end": 200 }]""", """[{ "start": 90, "end": 150 }, { "start": 150, "end": 210 }]""", "e: part 1 has no required credits")]
    public void Labels_RefuseAPartTheScorerCannotRead(string parts, string ignore, string refusal)
    {
        using var directory = new TempDirectory();

        var error = Assert.Throws<InvalidDataException>(() => ScoreAgainst(directory, $$"""{ "files": [{ "id": "e", "parts": {{parts}}, "ignore": {{ignore}} }] }"""));

        Assert.Contains(directory.Join("labels.json"), error.Message, StringComparison.Ordinal);
        Assert.Contains(refusal, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_WarnForATrapOverlappingAnIgnoreRange_AndAcceptPartsThatMeetAtABand()
    {
        // The parts meet where the first one's end band closes and the second one's start band
        // opens. The first trap runs 0.25 s into ignored credits; the second only touches others.
        using var directory = new TempDirectory();

        var lines = ScoreAgainst(directory, """
            { "files": [{ "id": "e",
              "parts": [{ "start": 100, "end": [200, 210] }, { "start": [210, 211], "end": 300 }],
              "ignore": [{ "start": 20, "end": 40 }, { "start": 300, "end": 400 }],
              "traps": [{ "start": 399.75, "end": 410, "note": "dim scene" }, { "start": 40, "end": 60 }] }] }
            """);

        Assert.Equal("warning: e: trap 399.75-410 overlaps ignore 300-400 by 0.25 s", Assert.Single(lines, line => line.StartsWith("warning:", StringComparison.Ordinal)));
        Assert.Contains("e: parts [missed, missed], story 0.00 s, credits missed 189.00 s", lines);
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
        Assert.All(episode.Calls, call => Assert.Equal(0, call.TimedOut));
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

    private static Task<RunResults> RunSeasonAsync(RunMode mode, string? introSeason, string? clipSeason)
        => new Runner(new PluginConfiguration(), repeats: 1, TextWriter.Null, mode).RunAsync(
            [new CorpusFile("intro", "../../../audio/big_buck_bunny_intro.mp3", 25, Season: introSeason), new CorpusFile("clip", "../../../audio/big_buck_bunny_clip.mp3", 60, Season: clipSeason)],
            CancellationToken.None);

    /// <summary>
    /// A 7 s card part from 100 to its end band, then a roll from 227 to 327, with the titles
    /// between them left for the caller to ignore.
    /// </summary>
    private static LabeledPart[] CardTitlesRoll(Boundary cardEnd)
        => [new LabeledPart(new Boundary(100, 103), cardEnd), new LabeledPart(new Boundary(227, 227.5), new Boundary(327, 327))];

    // Scores a run with no candidates for file "e" against the given labels file.
    private static string[] ScoreAgainst(TempDirectory directory, string labels)
    {
        Json.Write(directory.Join("run.json"), Run(Result("e")));
        File.WriteAllText(directory.Join("labels.json"), labels);
        using var output = new StringWriter();
        Commands.Score(["--results", directory.Join("run.json"), "--labels", directory.Join("labels.json")], output);
        return output.ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static EpisodeResult Result(string id, params Candidate[] combined)
        => new(id, 0, 450, combined, combined, null, false, 0, null, 0, []);

    // A file with no candidates whose black-interval probe had two processes killed at the timeout.
    private static EpisodeResult Unfinished(string id)
        => Result(id) with { Calls = [new CallTiming(nameof(IFFmpegService.ScanKeyframesAsync), 1, 2, null), new CallTiming(nameof(IFFmpegService.DetectBlackIntervalsAsync), 2, 600, null, TimedOut: 2)] };

    private static RunResults Run(params EpisodeResult[] episodes)
        => new("commit", "ffmpeg", "cpu", "os", 1, JsonSerializer.SerializeToElement(new PluginConfiguration(), Json.Options), episodes);
}
