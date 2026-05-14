// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Checks FFmpeg installation capabilities (chromaprint, silencedetect, etc.)
/// and provides diagnostic logs for the support bundle.
/// </summary>
public interface IFFmpegCapabilityService
{
    /// <summary>
    /// Check that the installed version of ffmpeg supports chromaprint.
    /// A successful result is cached for the lifetime of the service instance;
    /// failures are retried on every call so that installing or upgrading FFmpeg
    /// takes effect without restarting the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true if a compatible version of ffmpeg is installed, false on any error.</returns>
    Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets Chromaprint debugging logs.
    /// </summary>
    /// <returns>Markdown formatted logs.</returns>
    string GetChromaprintLogs();
}
