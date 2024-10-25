// Copyright (C) 2024 Intro-Skipper Contributors <intro-skipper.org>
// SPDX-License-Identifier: GNU General Public License v3.0 only.

namespace ConfusedPolarBear.Plugin.IntroSkipper;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using MediaBrowser.Model.Entities;
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
