// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using IntroSkipper.Configuration;

namespace CreditsRunner;

/// <summary>
/// The three commands: run the analyzer over a corpus, score a run against labels, and diff two
/// runs. A bad argument or input file throws <see cref="ArgumentException"/> before any work starts.
/// </summary>
internal static class Commands
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken)
    {
        var options = Options.Parse(args, "manifest", "out", "repeat", "minimum-credits-duration", "ffmpeg");
        var manifestPath = Path.GetFullPath(options.Required("manifest"));
        var outPath = Path.GetFullPath(options.Required("out"));
        var repeats = options.Int("repeat") ?? 1;
        if (repeats < 1)
        {
            throw new ArgumentException("--repeat must be at least 1");
        }

        var config = new PluginConfiguration();
        if (options.Int("minimum-credits-duration") is { } minimumDuration)
        {
            config.MinimumCreditsDuration = minimumDuration;
        }

        // A missing file would scan as a file without credits, so the run refuses it, as the
        // analysis pass skips it.
        var directory = Path.GetDirectoryName(manifestPath)!;
        var files = Json.Read<Manifest>(manifestPath).Files
            .Select(file => file with { Path = Path.GetFullPath(file.Path, directory) })
            .ToList();
        var bad = files.Where(file => !File.Exists(file.Path) || file.Duration <= 0).Select(file => file.Id).ToList();
        if (bad.Count > 0)
        {
            throw new ArgumentException($"missing file or no duration: {string.Join(", ", bad)}");
        }

        // The results are written after the whole run, over whatever --out names.
        if (files.Select(file => file.Path).Append(manifestPath).Contains(outPath, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--out must not name the manifest or a corpus file");
        }

        if (options.Optional("ffmpeg") is { } ffmpegDirectory)
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetFullPath(ffmpegDirectory) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var results = await new Runner(config, repeats, output).RunAsync(files, cancellationToken).ConfigureAwait(false);
        Json.Write(outPath, results);
        return 0;
    }

    public static int Score(IReadOnlyList<string> args, TextWriter output)
    {
        var options = Options.Parse(args, "results", "labels");
        var results = Json.Read<RunResults>(options.Required("results"));
        var labels = Labels(options);
        output.WriteLine($"{results.Commit} | {results.Ffmpeg} | {results.Cpu}");

        List<EpisodeScore> scores = [];
        foreach (var episode in results.Episodes)
        {
            if (!labels.TryGetValue(episode.Id, out var label))
            {
                output.WriteLine($"{episode.Id}: no label");
                continue;
            }

            var score = Scorer.Score(label, episode);
            scores.Add(score);
            output.WriteLine($"{episode.Id}: {Describe(score)}{(episode.Nondeterministic ? " (nondeterministic)" : string.Empty)}");
        }

        output.WriteLine();
        WriteSummaries(output, scores);
        output.WriteLine();
        foreach (var trigger in results.Episodes.SelectMany(episode => episode.Calls).GroupBy(call => call.Trigger))
        {
            output.WriteLine(Invariant($"{trigger.Key}: {trigger.Count()} calls, {trigger.Sum(call => call.Processes)} ffmpeg, {trigger.Sum(call => call.WallSeconds):F1} s"));
        }

        output.WriteLine(Invariant($"total: {results.Episodes.Sum(episode => episode.WallSeconds):F1} s wall, {Seconds(results.Episodes.Select(episode => episode.CpuSeconds))} ffmpeg CPU, {results.Episodes.Sum(episode => episode.PluginCpuSeconds):F1} s plugin CPU"));
        return 0;
    }

    /// <summary>
    /// Lists every file whose candidates changed, flags every regression, and prints both runs'
    /// totals. Returns 1 when it finds a regression.
    /// </summary>
    public static int Diff(IReadOnlyList<string> args, TextWriter output)
    {
        var options = Options.Parse(args, "baseline", "candidate", "labels");
        var baseline = Json.Read<RunResults>(options.Required("baseline"));
        var candidate = Json.Read<RunResults>(options.Required("candidate"));
        var labels = Labels(options);

        // Anything else that differs between the runs can change candidates or times too.
        if (!JsonElement.DeepEquals(baseline.Configuration, candidate.Configuration))
        {
            output.WriteLine("warning: the runs used different configurations");
        }

        foreach (var (what, before, after) in new[] { ("ffmpeg", baseline.Ffmpeg, candidate.Ffmpeg), ("CPU", baseline.Cpu, candidate.Cpu), ("repeats", $"{baseline.Repeats}", $"{candidate.Repeats}") })
        {
            if (before != after)
            {
                output.WriteLine($"warning: {what} differs: {before} -> {after}");
            }
        }

        var old = baseline.Episodes.ToDictionary(episode => episode.Id);
        var current = candidate.Episodes.ToDictionary(episode => episode.Id);
        List<EpisodeScore> baselineScores = [];
        List<EpisodeScore> candidateScores = [];
        var regressions = 0;
        foreach (var id in old.Keys.Union(current.Keys))
        {
            if (!old.TryGetValue(id, out var before) || !current.TryGetValue(id, out var after))
            {
                output.WriteLine($"{id}: only in the {(old.ContainsKey(id) ? "baseline" : "candidate")}");
                continue;
            }

            if (before.Failure != after.Failure || !before.Combined.SequenceEqual(after.Combined))
            {
                output.WriteLine($"{id}: {Candidates(before)} -> {Candidates(after)}");
            }

            if (after.Nondeterministic)
            {
                output.WriteLine($"{id}: nondeterministic in the candidate");
            }

            if (!labels.TryGetValue(id, out var label))
            {
                continue;
            }

            var (beforeScore, afterScore) = (Scorer.Score(label, before), Scorer.Score(label, after));
            baselineScores.Add(beforeScore);
            candidateScores.Add(afterScore);
            foreach (var regression in Differ.Regressions(beforeScore, afterScore))
            {
                regressions++;
                output.WriteLine($"  REGRESSION {id}: {regression}");
            }
        }

        output.WriteLine();
        output.WriteLine($"baseline {baseline.Commit}");
        WriteSummaries(output, baselineScores);
        output.WriteLine();
        output.WriteLine($"candidate {candidate.Commit}");
        WriteSummaries(output, candidateScores);
        output.WriteLine();
        output.WriteLine(Invariant($"{regressions} regressions, wall {baseline.Episodes.Sum(e => e.WallSeconds):F1} -> {candidate.Episodes.Sum(e => e.WallSeconds):F1} s, ffmpeg CPU {Seconds(baseline.Episodes.Select(e => e.CpuSeconds))} -> {Seconds(candidate.Episodes.Select(e => e.CpuSeconds))}"));
        return regressions > 0 ? 1 : 0;
    }

    public static int Usage(TextWriter output)
    {
        output.WriteLine("""
            CreditsRunner run --manifest <manifest.json> --out <results.json> [--repeat <n>] [--minimum-credits-duration <s>] [--ffmpeg <dir>]
            CreditsRunner score --results <results.json> --labels <labels.json>
            CreditsRunner diff --baseline <results.json> --candidate <results.json> --labels <labels.json>
            """);
        return 2;
    }

    private static Dictionary<string, Label> Labels(Options options)
        => Json.Read<LabelSet>(options.Required("labels")).Files.ToDictionary(label => label.Id);

    private static void WriteSummaries(TextWriter output, IReadOnlyList<EpisodeScore> scores)
    {
        (string Name, IReadOnlyList<EpisodeScore> Group)[] groups = [("all", scores), ("tuning", [.. scores.Where(s => !s.Holdout)]), ("holdout", [.. scores.Where(s => s.Holdout)])];
        foreach (var (name, group) in groups)
        {
            if (group.Count == 0)
            {
                continue;
            }

            var summary = Scorer.Summarize(group);
            output.WriteLine(Invariant($"{name}: {summary.Files} files, {summary.Failures} failed, {summary.Parts} parts, {summary.Missed} missed, {summary.FalseParts} false, start {summary.MeanStartError:+0.00;-0.00} (|{summary.MeanAbsoluteStartError:F2}|) s, end {summary.MeanEndError:+0.00;-0.00} (|{summary.MeanAbsoluteEndError:F2}|) s, story skipped {summary.StorySkipped:F1} s, credits missed {summary.CreditsMissed:F1} s"));
            output.WriteLine("  hit rate " + string.Join(", ", summary.HitRates.Select(rate => Invariant($"{rate.Tolerance} s {rate.Start:P0}/{rate.End:P0}"))));
        }
    }

    private static string Describe(EpisodeScore score)
    {
        var parts = score.Parts.Select(part => part.Missed ? "missed" : Invariant($"{part.StartError:+0.00;-0.00}/{part.EndError:+0.00;-0.00}"));
        return (score.Failed ? "FAILED, " : string.Empty)
            + $"parts [{string.Join(", ", parts)}]"
            + (score.FalseParts.Count > 0 ? $", false {string.Join(", ", score.FalseParts)}" : string.Empty)
            + Invariant($", story {score.StorySkipped:F2} s, credits missed {score.CreditsMissed:F2} s");
    }

    private static string Candidates(EpisodeResult episode)
        => episode.Failure ?? (episode.Combined.Count == 0 ? "none" : string.Join(", ", episode.Combined));

    private static string Seconds(IEnumerable<double?> values)
    {
        var all = values.ToArray();
        return all.Any(value => value is null) ? "n/a" : Invariant($"{all.Sum(value => value!.Value):F1} s");
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A command's options as <c>--name value</c> pairs. Any name the command does not take is an error.
/// </summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values;

    private Options(Dictionary<string, string> values) => _values = values;

    public static Options Parse(IReadOnlyList<string> args, params string[] allowed)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count)
            {
                throw new ArgumentException($"expected --name value, got '{args[i]}'");
            }

            var name = args[i][2..];
            if (!allowed.Contains(name))
            {
                throw new ArgumentException($"unknown option --{name}; this command takes --{string.Join(", --", allowed)}");
            }

            values[name] = args[i + 1];
        }

        return new Options(values);
    }

    public string Required(string name)
        => _values.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required");

    public string? Optional(string name) => _values.GetValueOrDefault(name);

    public int? Int(string name)
        => Optional(name) is not { } text ? null
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
            : throw new ArgumentException($"--{name} must be a whole number");
}
