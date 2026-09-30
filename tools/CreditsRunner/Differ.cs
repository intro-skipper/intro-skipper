// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace CreditsRunner;

/// <summary>
/// Finds regressions between two scores of the same file.
/// </summary>
/// <remarks>
/// A regression is a newly failed file, a newly missed part, a new false part, more story skipped,
/// or a start or end error that grew in size. Growth up to <see cref="Slack"/> seconds is noise.
/// </remarks>
internal static class Differ
{
    /// <summary>
    /// The seconds a boundary error or the story skipped may grow before it counts.
    /// </summary>
    public const double Slack = 0.5;

    /// <summary>
    /// Lists every regression of a candidate score against a baseline score.
    /// </summary>
    /// <param name="baseline">The baseline score.</param>
    /// <param name="candidate">The candidate score of the same file, against the same label.</param>
    /// <returns>One line per regression; empty when there is none.</returns>
    public static IEnumerable<string> Regressions(EpisodeScore baseline, EpisodeScore candidate)
    {
        if (candidate.Failed && !baseline.Failed)
        {
            yield return "the analyzer now fails";
        }

        for (var i = 0; i < Math.Min(baseline.Parts.Count, candidate.Parts.Count); i++)
        {
            var (before, after) = (baseline.Parts[i], candidate.Parts[i]);
            if (after.Missed && !before.Missed)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"part {i + 1} is now missed");
            }
            else if (!after.Missed && !before.Missed)
            {
                if (Grew(before.StartError!.Value, after.StartError!.Value))
                {
                    yield return string.Create(CultureInfo.InvariantCulture, $"part {i + 1} start error {before.StartError:+0.00;-0.00} -> {after.StartError:+0.00;-0.00} s");
                }

                if (Grew(before.EndError!.Value, after.EndError!.Value))
                {
                    yield return string.Create(CultureInfo.InvariantCulture, $"part {i + 1} end error {before.EndError:+0.00;-0.00} -> {after.EndError:+0.00;-0.00} s");
                }
            }
        }

        foreach (var part in candidate.FalseParts.Where(part => !baseline.FalseParts.Any(old => old.Start < part.End && part.Start < old.End)))
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"new false part {part.Start:F2}-{part.End:F2}");
        }

        if (candidate.StorySkipped - baseline.StorySkipped > Slack)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"story skipped {baseline.StorySkipped:F2} -> {candidate.StorySkipped:F2} s");
        }
    }

    private static bool Grew(double before, double after) => Math.Abs(after) - Math.Abs(before) > Slack;
}
