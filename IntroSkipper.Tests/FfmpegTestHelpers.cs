// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Helpers for tests that run the real ffmpeg against the fixture media under the repo root.
/// </summary>
internal static class FfmpegTestHelpers
{
    /// <summary>
    /// Creates a real <see cref="FFmpegService"/> over a fresh temp cache database path.
    /// </summary>
    /// <param name="versionProbe">Replaces the ffmpeg version probe; <see langword="null"/> runs the real one.</param>
    /// <param name="versionProbeTimeout">Bounds one probe attempt.</param>
    /// <returns>The service.</returns>
    internal static FFmpegService CreateFFmpegService(Func<CancellationToken, Task<bool>>? versionProbe = null, TimeSpan? versionProbeTimeout = null)
        => new(NullLogger<FFmpegService>.Instance, DatabaseTestHelpers.CreateTempCacheService(), versionProbe, versionProbeTimeout);

    /// <summary>
    /// Queues a fixture file for analysis.
    /// </summary>
    /// <param name="relativePath">Path under the repo root, e.g. <c>video/credits.mp4</c>.</param>
    /// <returns>An episode whose <see cref="QueuedEpisode.Path"/> resolves from the test output directory.</returns>
    internal static QueuedEpisode QueueFile(string relativePath) => new()
    {
        EpisodeId = Guid.NewGuid(),
        Name = Path.GetFileName(relativePath),
        Path = "../../../" + relativePath,
    };
}
