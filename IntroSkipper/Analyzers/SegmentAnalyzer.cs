// Copyright (C) 2024 Intro-Skipper Contributors <intro-skipper.org>
// SPDX-License-Identifier: GNU General Public License v3.0 only.

using System.Collections.Generic;
using System.Threading;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Chapter name analyzer.
/// </summary>
public class SegmentAnalyzer : IMediaFileAnalyzer
{
    private readonly ILogger<SegmentAnalyzer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentAnalyzer"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public SegmentAnalyzer(ILogger<SegmentAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<QueuedEpisode> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        return analysisQueue;
    }
}
