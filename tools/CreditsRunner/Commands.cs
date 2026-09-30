// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using IntroSkipper.Configuration;

namespace CreditsRunner;

/// <summary>
/// The three commands: run the analyzer over a corpus, score a run against labels, and diff two runs.
/// </summary>
internal static class Commands
{
    /// <summary>
    /// Runs the analyzer over every file in a manifest and writes the results.
    /// </summary>
    /// <param name="options"><c>--manifest</c>, <c>--out</c>, and optionally <c>--repeat</c>, <c>--minimum-credits-duration</c> and <c>--ffmpeg</c>, a directory put first on PATH.</param>
    /// <param name="output">Where progress goes.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(Options options, TextWriter output)
    {
        var manifestPath = Path.GetFullPath(options.Required("manifest"));
        var outPath = options.Required("out");
        var repeats = options.Int("repeat") ?? 1;
        if (repeats < 1)
        {
            throw new ArgumentException("--repeat must be at least 1");
        }

        if (options.Optional("ffmpeg") is { } ffmpegDirectory)
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetFullPath(ffmpegDirectory) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        }

        var config = new PluginConfiguration();
        if (options.Int("minimum-credits-duration") is { } minimumDuration)
        {
            config.MinimumCreditsDuration = minimumDuration;
        }

        var directory = Path.GetDirectoryName(manifestPath)!;
        var files = Json.Read<Manifest>(manifestPath).Files
            .Select(file => file with { Path = Path.GetFullPath(file.Path, directory) })
            .ToList();

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var results = await new Runner(config, repeats, output).RunAsync(files, stop.Token).ConfigureAwait(false);
        Json.Write(outPath, results);
        return 0;
    }

    /// <summary>
    /// Scores a run against labels and prints a line per file and the totals.
    /// </summary>
    /// <param name="options"><c>--results</c> and <c>--labels</c>.</param>
    /// <param name="output">Where the report goes.</param>
    /// <returns>The exit code.</returns>
    public static int Score(Options options, TextWriter output)
    {
        var results = Json.Read<RunResults>(options.Required("results"));
        var labels = Labels(options);
        output.WriteLine($"{results.Plugin} | {results.Ffmpeg} | {results.Cpu}");

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
    /// Lists every file whose candidates changed between two runs, flags every regression, and
    /// prints both runs' totals.
    /// </summary>
    /// <param name="options"><c>--baseline</c>, <c>--candidate</c> and <c>--labels</c>.</param>
    /// <param name="output">Where the report goes.</param>
    /// <returns>1 when any regression was found, otherwise 0.</returns>
    public static int Diff(Options options, TextWriter output)
    {
        var baseline = Json.Read<RunResults>(options.Required("baseline"));
        var candidate = Json.Read<RunResults>(options.Required("candidate"));
        var labels = Labels(options);
        if (baseline.Configuration.GetRawText() != candidate.Configuration.GetRawText())
        {
            output.WriteLine("warning: the runs used different configurations");
        }

        var before = baseline.Episodes.ToDictionary(episode => episode.Id);
        var after = candidate.Episodes.ToDictionary(episode => episode.Id);
        List<EpisodeScore> baselineScores = [];
        List<EpisodeScore> candidateScores = [];
        var regressions = 0;
        foreach (var id in before.Keys.Union(after.Keys))
        {
            if (!before.TryGetValue(id, out var old) || !after.TryGetValue(id, out var current))
            {
                output.WriteLine($"{id}: only in the {(before.ContainsKey(id) ? "baseline" : "candidate")}");
                continue;
            }

            var changed = old.Failure != current.Failure || !old.Combined.SequenceEqual(current.Combined);
            if (changed)
            {
                output.WriteLine($"{id}: {Candidates(old)} -> {Candidates(current)}");
            }

            if (current.Nondeterministic)
            {
                output.WriteLine($"{id}: nondeterministic in the candidate");
            }

            if (!labels.TryGetValue(id, out var label))
            {
                continue;
            }

            var (oldScore, newScore) = (Scorer.Score(label, old), Scorer.Score(label, current));
            baselineScores.Add(oldScore);
            candidateScores.Add(newScore);
            foreach (var regression in Differ.Regressions(oldScore, newScore))
            {
                regressions++;
                output.WriteLine($"  REGRESSION {id}: {regression}");
            }
        }

        output.WriteLine();
        output.WriteLine($"baseline {baseline.Plugin}");
        WriteSummaries(output, baselineScores);
        output.WriteLine();
        output.WriteLine($"candidate {candidate.Plugin}");
        WriteSummaries(output, candidateScores);
        output.WriteLine();
        output.WriteLine(Invariant($"{regressions} regressions, wall {baseline.Episodes.Sum(e => e.WallSeconds):F1} -> {candidate.Episodes.Sum(e => e.WallSeconds):F1} s, ffmpeg CPU {Seconds(baseline.Episodes.Select(e => e.CpuSeconds))} -> {Seconds(candidate.Episodes.Select(e => e.CpuSeconds))}"));
        return regressions > 0 ? 1 : 0;
    }

    /// <summary>
    /// Prints how to call the runner.
    /// </summary>
    /// <param name="output">Where the text goes.</param>
    /// <returns>The exit code for a wrong call.</returns>
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
            + (score.FalseParts.Count > 0 ? $", false {string.Join(", ", score.FalseParts.Select(p => Invariant($"{p.Start:F2}-{p.End:F2}")))}" : string.Empty)
            + Invariant($", story {score.StorySkipped:F2} s, credits missed {score.CreditsMissed:F2} s");
    }

    private static string Candidates(EpisodeResult episode)
        => episode.Failure ?? (episode.Combined.Count == 0 ? "none" : string.Join(", ", episode.Combined.Select(c => Invariant($"{c.Start:F2}-{c.End:F2}"))));

    private static string Seconds(IEnumerable<double?> values)
    {
        var all = values.ToArray();
        return all.Any(value => value is null) ? "n/a" : Invariant($"{all.Sum(value => value!.Value):F1} s");
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Command-line options in <c>--name value</c> pairs.
/// </summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values;

    private Options(Dictionary<string, string> values) => _values = values;

    /// <summary>
    /// Reads <c>--name value</c> pairs.
    /// </summary>
    /// <param name="args">The arguments after the command.</param>
    /// <returns>The options.</returns>
    /// <exception cref="ArgumentException">An argument is not a <c>--name value</c> pair.</exception>
    public static Options Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count)
            {
                throw new ArgumentException($"expected --name value, got '{args[i]}'");
            }

            values[args[i][2..]] = args[i + 1];
        }

        return new Options(values);
    }

    /// <summary>
    /// Gets a required option.
    /// </summary>
    /// <param name="name">The name without dashes.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentException">The option is missing.</exception>
    public string Required(string name)
        => _values.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required");

    /// <summary>
    /// Gets an optional option.
    /// </summary>
    /// <param name="name">The name without dashes.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    public string? Optional(string name) => _values.GetValueOrDefault(name);

    /// <summary>
    /// Gets an optional whole-number option.
    /// </summary>
    /// <param name="name">The name without dashes.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">The value is not a whole number.</exception>
    public int? Int(string name)
        => Optional(name) is not { } text ? null
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
            : throw new ArgumentException($"--{name} must be a whole number");
}
