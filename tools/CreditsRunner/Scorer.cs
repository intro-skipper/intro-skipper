// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace CreditsRunner;

/// <summary>
/// How one labeled part came out: signed errors, negative when the prediction starts or ends
/// early, or both <see langword="null"/> when no prediction matched the part. An error is the
/// seconds the boundary costs: story for an early start or a late end, required credits for a
/// late start or an early end. Ignore ranges and other parts cost nothing.
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
    /// Scores one file. Pairs are taken by their overlap with the seconds the part always covers
    /// outside ignore ranges, largest first, so a prediction over only band or ignore time
    /// matches nothing. Each part matches at most one prediction, and a prediction matches one
    /// part or several of one block: neighbouring parts with at most <see cref="Slack"/> of story
    /// between them.
    /// </summary>
    public static EpisodeScore Score(Label label, EpisodeResult result)
    {
        var parts = label.Parts;
        var predictions = result.Combined;
        var ignore = Union(label.Ignore ?? []);
        var credits = Union([.. parts.Select(part => part.Outer), .. ignore]);
        var block = new int[parts.Count];
        for (var i = 1; i < parts.Count; i++)
        {
            var story = Length(Subtract([new Interval(parts[i - 1].End.Latest, parts[i].Start.Earliest)], credits));
            block[i] = block[i - 1] + (story > Slack ? 1 : 0);
        }

        var matchOfPart = new int?[parts.Count];
        var blockOfPrediction = new int?[predictions.Count];
        var pairs = parts
            .SelectMany((part, i) => predictions.Select((prediction, j) => (Part: i, Prediction: j, Overlap: Overlap(Subtract([part.Inner], ignore), prediction.Span))))
            .Where(pair => pair.Overlap > 0)
            .OrderByDescending(pair => pair.Overlap);
        foreach (var (part, prediction, _) in pairs)
        {
            if (matchOfPart[part] is null && (blockOfPrediction[prediction] ?? block[part]) == block[part])
            {
                matchOfPart[part] = prediction;
                blockOfPrediction[prediction] = block[part];
            }
        }

        var predicted = Union([.. predictions.Select(prediction => prediction.Span)]);
        return new EpisodeScore(
            label.Holdout,
            result.Failure is not null,
            [.. parts.Select((part, i) => matchOfPart[i] is { } j
                ? ScorePart(i, predictions[j].Span, i > 0 && matchOfPart[i - 1] == j, i + 1 < parts.Count && matchOfPart[i + 1] == j)
                : new PartScore(null, null))],
            [.. predictions.Where((prediction, j) => blockOfPrediction[j] is null && Length(Subtract([prediction.Span], credits)) > Slack)],
            Subtract(predicted, credits),
            Length(Subtract(Subtract(Union([.. parts.Select(part => part.Inner)]), ignore), predicted)));

        // A prediction shared with a neighbouring part is measured only inside the block, so
        // story before or after the block counts once, on its first or last part.
        PartScore ScorePart(int i, Interval span, bool sharedWithPrevious, bool sharedWithNext)
        {
            var (start, end) = (parts[i].Start, parts[i].End);
            var predictedStart = sharedWithPrevious ? Math.Max(span.Start, parts[i - 1].End.Latest) : span.Start;
            var predictedEnd = sharedWithNext ? Math.Min(span.End, parts[i + 1].Start.Earliest) : span.End;
            return new PartScore(
                predictedStart < start.Earliest ? -Length(Subtract([new Interval(predictedStart, start.Earliest)], credits))
                    : predictedStart > start.Latest ? Length(Subtract([new Interval(start.Latest, predictedStart)], ignore))
                    : 0,
                predictedEnd > end.Latest ? Length(Subtract([new Interval(end.Latest, predictedEnd)], credits))
                    : predictedEnd < end.Earliest ? -Length(Subtract([new Interval(predictedEnd, end.Earliest)], ignore))
                    : 0);
        }
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

    private static double Overlap(IEnumerable<Interval> spans, Interval span) => spans.Sum(piece => new Interval(Math.Max(piece.Start, span.Start), Math.Min(piece.End, span.End)).Length);

    private static double Mean(IEnumerable<double> values)
    {
        var all = values.ToArray();
        return all.Length == 0 ? 0 : all.Average();
    }

    private static double Share(IReadOnlyList<PartScore> parts, Func<PartScore, bool> hit)
        => parts.Count == 0 ? 0 : (double)parts.Count(hit) / parts.Count;
}
