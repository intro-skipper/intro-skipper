namespace ConfusedPolarBear.Plugin.IntroSkipper;

using System.Collections.ObjectModel;
using System.Threading;
using Microsoft.Extensions.Logging;

/// <summary>
/// Chapter name analyzer.
/// </summary>
public class SegmentAnalyzer : IMediaFileAnalyzer
{
    private ILogger<SegmentAnalyzer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentAnalyzer"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public SegmentAnalyzer(ILogger<SegmentAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ReadOnlyCollection<QueuedEpisode> AnalyzeMediaFiles(
        ReadOnlyCollection<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        return analysisQueue;
    }
}
