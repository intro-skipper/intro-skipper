// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Stores authoritative SkipMe ranges without local boundary adjustments. Rejected ranges
/// still settle the mode, so local detection cannot resurrect a user-suppressed match.
/// </summary>
internal sealed class SkipMeAnalyzer(IIntroSkipperDatabase database) : IMediaFileAnalyzer
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        foreach (var episode in analysisQueue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!episode.NeedsAnalysis(mode) || episode.SkipMe?.HasSegments(mode) != true)
            {
                continue;
            }

            if (mode == AnalysisMode.Preview)
            {
                await database.ReplaceAutoSegmentsAsync(episode.EpisodeId, mode, [], SegmentSource.CreditsDerived, episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
            }

            var written = await database.ReplaceAutoSegmentsAsync(
                episode.EpisodeId,
                mode,
                episode.SkipMe.GetSegments(mode),
                SegmentSource.SkipMe,
                episode.AnalysisConfigHash,
                cancellationToken).ConfigureAwait(false);
            episode.SetAnalyzed(mode, written > 0 ? EpisodeState.Analyzed : EpisodeState.NoSegments);
        }

        return analysisQueue;
    }
}
