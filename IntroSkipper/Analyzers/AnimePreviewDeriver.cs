// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Derives an episode's Preview segment from the end of its last credits block to the end of the episode.
/// </summary>
internal static class AnimePreviewDeriver
{
    /// <summary>
    /// Tolerance (seconds) when comparing an existing Preview's start to a newly-computed
    /// credits.End. Chromaprint timestamps are quantised to ~0.124 s and a sub-second delta has
    /// no user-visible effect, so treat "close enough" as equal for idempotency.
    /// </summary>
    private const double StartTolerance = 0.5;

    /// <summary>
    /// Creates or refreshes the Preview segment of every episode whose credits end before the episode does.
    /// </summary>
    /// <remarks>
    /// An episode with a user-provided Preview is skipped: the admission gate only drops a derived
    /// preview that strictly overlaps it, so without this guard a non-overlapping manual Preview
    /// would gain a second, automatic one beside it, and the episode's UserProvided state would be
    /// overwritten with Analyzed. A derived preview that overlaps a tombstone is dropped by
    /// <see cref="AutoSegmentAdmissionPolicy"/>; the episode still counts as analyzed, since
    /// re-running would not change the gate's answer. An episode with any other automatic
    /// Preview, a subtitle or chapter match, is skipped too. The facade ranks a derived preview
    /// below both and would refuse the write, and the episode stays unsettled so its Preview
    /// pass can retire that row once it is stale and then derive the preview.
    /// </remarks>
    /// <param name="database">Segment database facade.</param>
    /// <param name="items">Episodes whose Credits or Preview mode was just analyzed.</param>
    /// <param name="minimumDuration">The minimum preview duration in seconds; shorter spans derive nothing.</param>
    /// <param name="cancellationToken">Cancellation token; stops the loop before the next episode.</param>
    /// <param name="settlePreviewState">
    /// <see langword="true"/> to mark each episode that gets a derived preview as analyzed for
    /// Preview; <see langword="false"/> to leave every Preview state as it is, for a credits pass
    /// followed by subtitle Preview detection, which can still supersede the derived preview.
    /// </param>
    /// <returns>A task that completes when every episode has been considered.</returns>
    internal static async Task DeriveAsync(
        IntroSkipperDatabase database,
        IReadOnlyList<QueuedEpisode> items,
        int minimumDuration,
        CancellationToken cancellationToken,
        bool settlePreviewState = true)
    {
        foreach (var episode in items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Any other preview wins, and the episode stays unsettled: its Preview pass may
            // delete a stale chapter or subtitle row, and only an unsettled episode gets that
            // pass's analyzers and derive afterwards.
            var dbSegments = await database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (dbSegments.Any(s => s.Type == AnalysisMode.Preview && s.Source != SegmentSource.CreditsDerived))
            {
                continue;
            }

            // Rows that touch or overlap, as adjusted neighbours can, form one credits run. The
            // post-credits preview begins after the last run and reaches the episode end.
            var credits = CreditsRuns(dbSegments).LastOrDefault();
            var previewEnd = episode.Duration;
            List<Segment> previews = [.. dbSegments
                .Where(s => s.Type == AnalysisMode.Preview)
                .Select(s => s.ToSegment())];

            var preview = Compute(episode.EpisodeId, previewEnd, credits, previews, minimumDuration);
            if (preview is null)
            {
                // Nothing derivable (no credits, or too little between the credits and the
                // preview end) leaves a preview derived by an earlier run stale: clear it so it
                // cannot overlap the credits that replaced its source.
                if ((credits is null || !credits.Valid || previewEnd - credits.End < minimumDuration)
                    && dbSegments.Any(s => s.Type == AnalysisMode.Preview && s.Source == SegmentSource.CreditsDerived))
                {
                    await database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Preview, [], SegmentSource.CreditsDerived, episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            await database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Preview, [preview], SegmentSource.CreditsDerived, episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
            if (settlePreviewState && episode.GetAnalyzed(AnalysisMode.Preview) != EpisodeState.AnalysisFailed)
            {
                episode.SetAnalyzed(AnalysisMode.Preview, EpisodeState.Analyzed);
            }
        }
    }

    /// <summary>
    /// Merges the episode's active credits rows into runs: rows that overlap or touch form one.
    /// </summary>
    /// <param name="segments">The episode's stored segments.</param>
    /// <returns>The runs in file seconds, ordered by start.</returns>
    internal static List<Segment> CreditsRuns(IReadOnlyList<DbSegment> segments)
    {
        var runs = new List<Segment>();
        foreach (var row in segments.Where(s => s.Type == AnalysisMode.Credits && s.State == SegmentState.Active).OrderBy(s => s.StartTicks))
        {
            var segment = row.ToSegment();
            if (runs.Count > 0 && segment.Start <= runs[^1].End)
            {
                runs[^1] = new Segment(segment.EpisodeId, new TimeRange(runs[^1].Start, Math.Max(runs[^1].End, segment.End)));
                continue;
            }

            runs.Add(segment);
        }

        return runs;
    }

    /// <summary>
    /// Decides whether a credits-derived Preview segment needs to be written for an episode, and builds it.
    /// </summary>
    /// <remarks>
    /// Returns a new Segment when the Preview is missing, its Start no longer matches the current
    /// credits.End (e.g. because settings changed and Credits was re-analyzed), or its End no longer
    /// matches the episode end (e.g. because the underlying media file was replaced).
    /// Returns <see langword="null"/> when there are no valid credits, less than
    /// <paramref name="minimumDuration"/> remains before the preview end, or any existing Preview
    /// already matches both the current credits.End and the preview end within <see cref="StartTolerance"/>.
    /// </remarks>
    /// <param name="episodeId">Episode id.</param>
    /// <param name="previewEnd">Episode duration, which is the end of the post-credits preview.</param>
    /// <param name="credits">The credits run feeding the preview, or <see langword="null"/>.</param>
    /// <param name="existingPreviews">All current Preview segments of the episode.</param>
    /// <param name="minimumDuration">The minimum preview duration in seconds.</param>
    /// <returns>Segment to write, or <see langword="null"/> when no write is needed.</returns>
    internal static Segment? Compute(
        Guid episodeId,
        double previewEnd,
        Segment? credits,
        IReadOnlyCollection<Segment> existingPreviews,
        int minimumDuration)
    {
        if (credits is null || !credits.Valid || previewEnd - credits.End < minimumDuration)
        {
            return null;
        }

        foreach (var existing in existingPreviews)
        {
            if (existing.Valid
                && Math.Abs(existing.Start - credits.End) <= StartTolerance
                && Math.Abs(existing.End - previewEnd) <= StartTolerance)
            {
                return null;
            }
        }

        return new Segment(episodeId, new TimeRange(credits.End, previewEnd));
    }
}
