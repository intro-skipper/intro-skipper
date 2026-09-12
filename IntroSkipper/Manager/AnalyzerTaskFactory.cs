// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.ScheduledTasks;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Creates the per-run analyzer task. <see cref="BaseItemAnalyzerTask"/> holds per-run
/// state (memoized ffmpeg validity, captured configuration), so it cannot be a DI
/// singleton; this factory owns its dependency set instead, so adding a dependency means
/// changing this one class rather than every construction site.
/// </summary>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="seasonResolver">Season resolver.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="cacheService">Detection cache service.</param>
/// <param name="cacheDatabase">Detection cache database facade.</param>
/// <param name="database">Segment database facade.</param>
public class AnalyzerTaskFactory(
    ILoggerFactory loggerFactory,
    SeasonResolver seasonResolver,
    IFFmpegService ffmpegService,
    DetectionCacheService cacheService,
    IDetectionCacheDatabase cacheDatabase,
    IIntroSkipperDatabase database)
{
    /// <summary>
    /// Creates a fresh analyzer task for one analysis run.
    /// </summary>
    /// <returns>The analyzer task.</returns>
    internal BaseItemAnalyzerTask CreateAnalyzerTask()
        => new(
            loggerFactory.CreateLogger<BaseItemAnalyzerTask>(),
            loggerFactory,
            seasonResolver,
            ffmpegService,
            cacheService,
            cacheDatabase,
            database);
}
