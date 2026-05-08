// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Creates analyzer tasks with their shared Jellyfin and FFmpeg dependencies.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="BaseItemAnalyzerTaskFactory"/> class.
/// </remarks>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="libraryManager">Library manager.</param>
/// <param name="providerManager">Provider manager.</param>
/// <param name="fileSystem">File system.</param>
/// <param name="mediaSegmentUpdateManager">Media segment update manager.</param>
/// <param name="capabilityService">FFmpeg capability service.</param>
/// <param name="cacheService">Detection cache service.</param>
/// <param name="detectionService">Media detection service.</param>
public sealed class BaseItemAnalyzerTaskFactory(
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IFileSystem fileSystem,
    MediaSegmentUpdateManager mediaSegmentUpdateManager,
    IFFmpegCapabilityService capabilityService,
    IDetectionCacheService cacheService,
    IMediaDetectionService detectionService)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IProviderManager _providerManager = providerManager;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager = mediaSegmentUpdateManager;
    private readonly IFFmpegCapabilityService _capabilityService = capabilityService;
    private readonly IDetectionCacheService _cacheService = cacheService;
    private readonly IMediaDetectionService _detectionService = detectionService;

    /// <summary>
    /// Creates a new analyzer task instance.
    /// </summary>
    /// <param name="logger">The logger to use for task-level messages.</param>
    /// <returns>A configured analyzer task.</returns>
    public BaseItemAnalyzerTask Create(ILogger logger)
        => new(
            logger,
            _loggerFactory,
            _libraryManager,
            _providerManager,
            _fileSystem,
            _mediaSegmentUpdateManager,
            _capabilityService,
            _cacheService,
            _detectionService);
}
