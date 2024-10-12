using System.Collections.Generic;
using System.Threading;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
using Jellyfin.Data.Enums;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Analyzers;

/// <summary>
/// Chapter name analyzer.
/// </summary>
public class SegmentAnalyzer : IMediaFileAnalyzer
{
    /// <inheritdoc />
    public IReadOnlyList<QueuedEpisode> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        MediaSegmentType mode,
        CancellationToken cancellationToken)
    {
        return analysisQueue;
    }
}
