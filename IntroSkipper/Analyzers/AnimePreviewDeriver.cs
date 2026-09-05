// SPDX-FileCopyrightText: 2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Derives an anime episode's Preview segment: the tail of the episode after the final credits block.
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
    /// re-running would not change the gate's answer.
    /// </remarks>
    /// <param name="database">Segment database facade.</param>
    /// <param name="items">Episodes whose Credits mode was just analyzed.</param>
    /// <param name="cancellationToken">Cancellation token; stops the loop before the next episode.</param>
    /// <returns>A task that completes when every episode has been considered.</returns>
    internal static async Task DeriveAsync(IIntroSkipperDatabase database, IReadOnlyList<QueuedEpisode> items, CancellationToken cancellationToken)
    {
        foreach (var episode in items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var dbSegments = await database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (dbSegments.Any(s => s.Type == AnalysisMode.Preview && s.Source == SegmentSource.User))
            {
                continue;
            }

            var credits = dbSegments
                .Where(s => s.Type == AnalysisMode.Credits)
                .OrderBy(s => s.StartTicks)
                .LastOrDefault()?
                .ToSegment();
            var previews = dbSegments
                .Where(s => s.Type == AnalysisMode.Preview)
                .Select(s => s.ToSegment())
                .ToList();

            var preview = Compute(episode.EpisodeId, episode.Duration, credits, previews);
            if (preview is null)
            {
                continue;
            }

            await database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Preview, [preview], SegmentSource.CreditsDerived, episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
            episode.SetAnalyzed(AnalysisMode.Preview, EpisodeState.Analyzed);
        }
    }

    /// <summary>
    /// Decides whether an anime Preview segment needs to be written for an episode, and builds it.
    /// </summary>
    /// <remarks>
    /// Returns a new Segment when the Preview is missing, its Start no longer matches the current
    /// credits.End (e.g. because settings changed and Credits was re-analyzed), or its End no longer
    /// matches the episode duration (e.g. because the underlying media file was replaced).
    /// Returns <see langword="null"/> when there are no valid credits, the credits already cover the
    /// episode, or any existing Preview already matches both the current credits.End and the episode
    /// duration within <see cref="StartTolerance"/>.
    /// </remarks>
    /// <param name="episodeId">Episode id.</param>
    /// <param name="episodeDuration">Episode duration in seconds.</param>
    /// <param name="credits">The credits segment feeding the preview (the latest-start credits block), or <see langword="null"/>.</param>
    /// <param name="existingPreviews">All current Preview segments of the episode.</param>
    /// <returns>Segment to write, or <see langword="null"/> when no write is needed.</returns>
    internal static Segment? Compute(
        Guid episodeId,
        double episodeDuration,
        Segment? credits,
        IReadOnlyCollection<Segment> existingPreviews)
    {
        if (credits is null || !credits.Valid || credits.End >= episodeDuration)
        {
            return null;
        }

        foreach (var existing in existingPreviews)
        {
            if (existing.Valid
                && Math.Abs(existing.Start - credits.End) <= StartTolerance
                && Math.Abs(existing.End - episodeDuration) <= StartTolerance)
            {
                return null;
            }
        }

        return new Segment(episodeId, new TimeRange(credits.End, episodeDuration));
    }
}
