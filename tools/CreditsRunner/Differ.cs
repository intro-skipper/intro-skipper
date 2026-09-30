// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace CreditsRunner;

/// <summary>
/// Finds regressions between two scores of the same file against the same label. Per file, a
/// regression is a new failure, a new false part or new story skipped; per labeled part, a new
/// miss or a start or end error that grew. Up to <see cref="Scorer.Slack"/> seconds of growth is noise.
/// </summary>
internal static class Differ
{
    public static IEnumerable<string> Regressions(EpisodeScore baseline, EpisodeScore candidate)
    {
        if (candidate.Failed && !baseline.Failed)
        {
            yield return "the analyzer now fails";
        }

        for (var i = 0; i < candidate.Parts.Count; i++)
        {
            var (before, after) = (baseline.Parts[i], candidate.Parts[i]);
            if (after.Missed && !before.Missed)
            {
                yield return Invariant($"part {i + 1} is now missed");
            }
            else if (!after.Missed && !before.Missed)
            {
                if (Grew(before.StartError!.Value, after.StartError!.Value))
                {
                    yield return Invariant($"part {i + 1} start error {before.StartError:+0.00;-0.00} -> {after.StartError:+0.00;-0.00} s");
                }

                if (Grew(before.EndError!.Value, after.EndError!.Value))
                {
                    yield return Invariant($"part {i + 1} end error {before.EndError:+0.00;-0.00} -> {after.EndError:+0.00;-0.00} s");
                }
            }
        }

        foreach (var part in candidate.FalseParts.Where(part => !baseline.FalseParts.Any(old => old.Start < part.End && part.Start < old.End)))
        {
            yield return Invariant($"new false part {part}");
        }

        // Story the baseline did not skip, so a shift that skips new story counts even when the
        // total stays the same.
        var newStory = Scorer.Length(Scorer.Subtract(candidate.Story, baseline.Story));
        if (newStory > Scorer.Slack)
        {
            yield return Invariant($"{newStory:F2} s of new story skipped");
        }
    }

    private static bool Grew(double before, double after) => Math.Abs(after) - Math.Abs(before) > Scorer.Slack;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
