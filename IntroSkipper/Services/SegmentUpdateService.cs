// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// Default <see cref="ISegmentUpdateService"/> implementation. The decision logic runs inside the
/// store's write transaction (via the <see cref="ISegmentStore.ReplaceNonCommercialAsync"/>
/// callback), so rule evaluation and the write are atomic exactly as they were in the original
/// <c>Plugin.UpdateTimestampAsync</c> implementation.
/// </summary>
internal sealed partial class SegmentUpdateService : ISegmentUpdateService
{
    /// <summary>
    /// Tolerance used when comparing segment start/end times.
    /// </summary>
    internal const double SegmentComparisonEpsilon = 0.001;

    private readonly ISegmentStore _segmentStore;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentUpdateService"/> class.
    /// </summary>
    /// <param name="segmentStore">Segment store.</param>
    /// <param name="logger">Logger.</param>
    public SegmentUpdateService(ISegmentStore segmentStore, ILogger logger)
    {
        _segmentStore = segmentStore;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task UpdateTimestampAsync(Segment segment, AnalysisMode mode, bool isUserProvided = false, string configHash = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);

        try
        {
            var dbSegment = new DbSegment(segment, mode, isUserProvided, configHash);

            if (mode == AnalysisMode.Commercial)
            {
                await _segmentStore.TryAddCommercialAsync(dbSegment, SegmentComparisonEpsilon, cancellationToken).ConfigureAwait(false);
                return;
            }

            await _segmentStore
                .ReplaceNonCommercialAsync(dbSegment, context => ShouldPersist(context, segment, mode, isUserProvided), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailedToUpdateTimestamp(_logger, ex, segment.EpisodeId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task DeleteTimestampAsync(Guid itemId, AnalysisMode mode, Segment? segment = null, CancellationToken cancellationToken = default)
    {
        // Multiple commercial segments may exist per item; deleting them all without an explicit
        // match would discard more than the caller intended.
        if (segment is null && mode == AnalysisMode.Commercial)
        {
            return;
        }

        await _segmentStore.DeleteSegmentsAsync(itemId, mode, segment, SegmentComparisonEpsilon, cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldPersist(NonCommercialSegmentContext context, Segment segment, AnalysisMode mode, bool isUserProvided)
    {
        // Do not overwrite a user-provided segment with an analysis result.
        if (!isUserProvided && context.ExistingSegments.Any(s => s.IsUserProvided))
        {
            return false;
        }

        // Guard: prevent auto-detected credits from overlapping with the introduction.
        if (mode == AnalysisMode.Credits && !isUserProvided && context.StoredIntroduction is { } intro
            && segment.Start < intro.End && intro.Start < segment.End)
        {
            LogCreditsOverlapWithIntro(_logger, segment.EpisodeId);
            return false;
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping credits for episode {EpisodeId}: detected segment overlaps with introduction")]
    private static partial void LogCreditsOverlapWithIntro(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update timestamp for episode {EpisodeId}")]
    private static partial void LogFailedToUpdateTimestamp(ILogger logger, Exception ex, Guid episodeId);
}
