// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace CreditsRunner;

/// <summary>
/// How one labeled part came out: signed errors, negative when the prediction starts or ends
/// early, or both <see langword="null"/> when no prediction matched the part.
/// </summary>
internal sealed record PartScore(double? StartError, double? EndError)
{
    public bool Missed => StartError is null;
}

/// <summary>
/// How one corpus file came out against its label.
/// </summary>
/// <param name="Parts">One score per labeled part, in label order.</param>
/// <param name="FalseParts">Predictions that matched no part and skip more than <see cref="Scorer.Slack"/> of story.</param>
/// <param name="Story">The story the predictions cover: outside every part's widest span and every ignore range.</param>
/// <param name="CreditsMissed">Seconds of credits no prediction covers, counting only the span each part always covers.</param>
internal sealed record EpisodeScore(
    bool Holdout,
    bool Failed,
    IReadOnlyList<PartScore> Parts,
    IReadOnlyList<Candidate> FalseParts,
    IReadOnlyList<Interval> Story,
    double CreditsMissed)
{
    public double StorySkipped => Story.Sum(span => span.Length);
}

/// <param name="MeanStartError">The mean signed start error over matched parts.</param>
/// <param name="MeanEndError">The mean signed end error over matched parts.</param>
/// <param name="HitRates">Per tolerance, the share of parts whose start and whose end land within it; a missed part misses both.</param>
internal sealed record Summary(
    int Files,
    int Failures,
    int Parts,
    int Missed,
    int FalseParts,
    double MeanStartError,
    double MeanAbsoluteStartError,
    double MeanEndError,
    double MeanAbsoluteEndError,
    double StorySkipped,
    double CreditsMissed,
    IReadOnlyList<HitRate> HitRates);

internal sealed record HitRate(double Tolerance, double Start, double End);

/// <summary>
/// Scores results against labels. The predictions are the combined candidates.
/// </summary>
internal static class Scorer
{
    /// <summary>
    /// The seconds of story or of boundary error that count as noise, such as keyframe jitter.
    /// </summary>
    public const double Slack = 0.5;

    private static readonly double[] _tolerances = [0.5, 1, 2, 5];

    /// <summary>
    /// Scores one file. Each labeled part matches at most one prediction and each prediction at
    /// most one part, taking the pairs with the largest overlap with the part's widest span first.
    /// </summary>
    public static EpisodeScore Score(Label label, EpisodeResult result)
    {
        var parts = label.Parts;
        var predictions = result.Combined;
        var matchOfPart = new Candidate?[parts.Count];
        var matched = new bool[predictions.Count];
        var pairs = parts
            .SelectMany((part, i) => predictions.Select((prediction, j) => (Part: i, Prediction: j, Overlap: Overlap(part.Outer, prediction.Span))))
            .Where(pair => pair.Overlap > 0)
            .OrderByDescending(pair => pair.Overlap);
        foreach (var (part, prediction, _) in pairs)
        {
            if (matchOfPart[part] is null && !matched[prediction])
            {
                matchOfPart[part] = predictions[prediction];
                matched[prediction] = true;
            }
        }

        var credits = Union([.. parts.Select(part => part.Outer), .. label.Ignore ?? []]);
        var predicted = Union([.. predictions.Select(prediction => prediction.Span)]);
        return new EpisodeScore(
            label.Holdout,
            result.Failure is not null,
            [.. parts.Select((part, i) => matchOfPart[i] is { } prediction
                ? new PartScore(part.Start.ErrorOf(prediction.Start), part.End.ErrorOf(prediction.End))
                : new PartScore(null, null))],
            [.. predictions.Where((prediction, j) => !matched[j] && Length(Subtract([prediction.Span], credits)) > Slack)],
            Subtract(predicted, credits),
            Length(Subtract(Union([.. parts.Select(part => part.Inner)]), predicted)));
    }

    public static Summary Summarize(IReadOnlyList<EpisodeScore> scores)
    {
        var parts = scores.SelectMany(score => score.Parts).ToArray();
        var hits = parts.Where(part => !part.Missed).ToArray();
        return new Summary(
            scores.Count,
            scores.Count(score => score.Failed),
            parts.Length,
            parts.Length - hits.Length,
            scores.Sum(score => score.FalseParts.Count),
            Mean(hits.Select(part => part.StartError!.Value)),
            Mean(hits.Select(part => Math.Abs(part.StartError!.Value))),
            Mean(hits.Select(part => part.EndError!.Value)),
            Mean(hits.Select(part => Math.Abs(part.EndError!.Value))),
            scores.Sum(score => score.StorySkipped),
            scores.Sum(score => score.CreditsMissed),
            [.. _tolerances.Select(tolerance => new HitRate(
                tolerance,
                Share(parts, part => Math.Abs(part.StartError ?? double.PositiveInfinity) <= tolerance),
                Share(parts, part => Math.Abs(part.EndError ?? double.PositiveInfinity) <= tolerance)))]);
    }

    /// <summary>
    /// Merges spans in any order into sorted, disjoint, non-empty spans.
    /// </summary>
    public static List<Interval> Union(IEnumerable<Interval> spans)
    {
        List<Interval> union = [];
        foreach (var span in spans.Where(span => span.Length > 0).OrderBy(span => span.Start))
        {
            if (union.Count > 0 && span.Start <= union[^1].End)
            {
                union[^1] = union[^1] with { End = Math.Max(union[^1].End, span.End) };
            }
            else
            {
                union.Add(span);
            }
        }

        return union;
    }

    /// <summary>
    /// Removes one union from another. Both must come from <see cref="Union"/>.
    /// </summary>
    public static List<Interval> Subtract(IReadOnlyList<Interval> from, IReadOnlyList<Interval> remove)
    {
        List<Interval> rest = [];
        foreach (var span in from)
        {
            var start = span.Start;
            foreach (var cut in remove.Where(cut => cut.End > start && cut.Start < span.End))
            {
                if (cut.Start > start)
                {
                    rest.Add(new Interval(start, cut.Start));
                }

                start = Math.Max(start, cut.End);
            }

            if (start < span.End)
            {
                rest.Add(new Interval(start, span.End));
            }
        }

        return rest;
    }

    public static double Length(IEnumerable<Interval> spans) => spans.Sum(span => span.Length);

    private static double Overlap(Interval a, Interval b) => new Interval(Math.Max(a.Start, b.Start), Math.Min(a.End, b.End)).Length;

    private static double Mean(IEnumerable<double> values)
    {
        var all = values.ToArray();
        return all.Length == 0 ? 0 : all.Average();
    }

    private static double Share(IReadOnlyList<PartScore> parts, Func<PartScore, bool> hit)
        => parts.Count == 0 ? 0 : (double)parts.Count(hit) / parts.Count;
}
