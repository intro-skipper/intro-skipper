// SPDX-FileCopyrightText: 2026 rlauuzo
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
    /// Encodes a 64 by 36 clip at 24 fps of one colour that cuts to black, to a temp file the caller
    /// deletes. It is H.264 with a keyframe every two seconds and none at a scene cut, so a cut
    /// between two of those keyframes stays between them.
    /// </summary>
    /// <param name="colour">The colour before the cut, as the lavfi color source names it.</param>
    /// <param name="cut">The second the picture cuts to black.</param>
    /// <param name="seconds">The clip length.</param>
    /// <returns>The clip's path.</returns>
    internal static async Task<string> CutToBlackClipAsync(string colour, double cut, int seconds)
    {
        var path = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-cut.mkv");
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            [
                "-y", "-v", "error",
                "-f", "lavfi", "-i", FormattableString.Invariant($"color=c={colour}:s=64x36:r=24:d={seconds}"),
                "-vf", FormattableString.Invariant($"drawbox=c=black:t=fill:enable='gte(t,{cut})'"),
                "-pix_fmt", "yuv420p", "-c:v", "libx264", "-g", "48", "-sc_threshold", "0", "-bf", "0",
                path,
            ]);
        return path;
    }

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
