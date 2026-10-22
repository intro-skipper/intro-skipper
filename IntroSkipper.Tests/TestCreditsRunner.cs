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
        // the second part counts as missed, although its credits are covered.
        var label = new Label("episode", [new LabeledPart(new Boundary(100, 100), new Boundary(200, 200)), new LabeledPart(new Boundary(210, 210), new Boundary(300, 300))]);

        var score = Scorer.Score(label, Result("episode", new Candidate(100, 300, "Combined")));

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

        var score = Scorer.Score(label, Result("episode", new(100, 140, "BlackFrame"), new(160, 200, "BlackFrame")));

        Assert.Equal([new PartScore(0, -60)], score.Parts);
        Assert.Empty(score.FalseParts);
        Assert.Equal(0, score.StorySkipped);
        Assert.Equal(20, score.CreditsMissed, 6);
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

    [Fact]
    public async Task Run_IntroMode_RefusesRepeat_EvenAtOne()
    {
        // The refusal comes before the manifest is read, so none is needed.
        var refusal = await Assert.ThrowsAsync<ArgumentException>(() => Commands.RunAsync(["--manifest", "m.json", "--out", "out.json", "--mode", "intro", "--repeat", "1"], TextWriter.Null, CancellationToken.None));
        Assert.Contains("--repeat", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Results_WrittenBeforeModeAndNoChapters_ReadAsCreditsWithChapters()
    {
        using var directory = new TempDirectory();
        var path = directory.Join("old.json");
        var configuration = JsonSerializer.SerializeToElement(new PluginConfiguration(), Json.Options);
        var json = JsonSerializer.SerializeToNode(new RunResults("commit", "ffmpeg", "cpu", "os", 1, configuration, [Result("e")], AnalysisMode.Introduction, NoChapters: true), Json.Options)!.AsObject();
        Assert.True(json.Remove("mode") && json.Remove("noChapters"));
        File.WriteAllText(path, json.ToJsonString());

        var results = Json.Read<RunResults>(path);

        Assert.Equal((AnalysisMode.Credits, false), (results.Mode, results.NoChapters));
    }

    [Fact]
    public void Diff_RefusesRunsOfDifferentModes()
    {
        using var directory = new TempDirectory();
        var configuration = JsonSerializer.SerializeToElement(new PluginConfiguration(), Json.Options);
        Json.Write(directory.Join("credits.json"), new RunResults("commit", "ffmpeg", "cpu", "os", 1, configuration, [Result("e")]));
        Json.Write(directory.Join("intro.json"), new RunResults("commit", "ffmpeg", "cpu", "os", 1, configuration, [Result("e")], AnalysisMode.Introduction));
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
        var results = await RunIntroAsync("s1", "s1");
        var apart = await RunIntroAsync(null, null);

        Assert.All(results.Episodes, episode =>
        {
            Assert.Null(episode.Failure);
            var intro = Assert.Single(episode.Combined);
            Assert.Equal(("Chromaprint", 0.0), (intro.Source, intro.Start));
        });
        Assert.All(apart.Episodes, episode => Assert.Empty(episode.Combined));
    }

    [FactSkipFFmpegTests]
    public async Task Run_IntroMode_AttributesEachCallToItsEpisode()
    {
        var results = await RunIntroAsync("s1", "s1");

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

    private static Task<RunResults> RunIntroAsync(string? introSeason, string? clipSeason)
        => new Runner(new PluginConfiguration(), repeats: 1, TextWriter.Null, AnalysisMode.Introduction).RunAsync(
            [new CorpusFile("intro", "../../../audio/big_buck_bunny_intro.mp3", 25, Season: introSeason), new CorpusFile("clip", "../../../audio/big_buck_bunny_clip.mp3", 60, Season: clipSeason)],
            CancellationToken.None);

    private static EpisodeResult Result(string id, params Candidate[] combined)
        => new(id, 0, 450, combined, combined, null, false, 0, null, 0, []);

    // A file with no candidates whose black-interval probe had two processes killed at the timeout.
    private static EpisodeResult Unfinished(string id)
        => Result(id) with { Calls = [new CallTiming(nameof(IFFmpegService.ScanKeyframesAsync), 1, 2, null), new CallTiming(nameof(IFFmpegService.DetectBlackIntervalsAsync), 2, 600, null, TimedOut: 2)] };

    private static RunResults Run(params EpisodeResult[] episodes)
        => new("commit", "ffmpeg", "cpu", "os", 1, JsonSerializer.SerializeToElement(new PluginConfiguration(), Json.Options), episodes);
}
