// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Runs FFmpeg-based media detection operations (fingerprinting, silence detection,
/// black frame detection, key frame detection) with integrated caching.
/// </summary>
public interface IMediaDetectionService
{
    /// <summary>
    /// Fingerprints a queued episode asynchronously.
    /// </summary>
    /// <param name="episode">Queued episode to fingerprint.</param>
    /// <param name="mode">Portion of media file to fingerprint. Introduction = first 25% / 10 minutes and Credits = last 4 minutes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Numerical fingerprint points.</returns>
    Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects ranges of silence in the provided episode asynchronously.
    /// </summary>
    /// <param name="episode">Queued episode.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="mode">Analysis mode, used to correctly key the cache entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of TimeRange objects that are silent in the queued episode.</returns>
    Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the location of all black frames in a media file within a time range asynchronously.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="minimum">Percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="mode">Analysis mode, used to correctly key the cache entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of frames that are mostly black, with absolute media timestamps.</returns>
    Task<BlackFrame[]> DetectBlackFramesInRangeAsync(
        QueuedEpisode episode,
        TimeRange range,
        int minimum,
        int threshold,
        AnalysisMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the location of all black frames in a media file starting at the credits fingerprint position asynchronously.
    /// Scans only key frames for efficiency.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of frames that are mostly black, with absolute media timestamps.</returns>
    Task<BlackFrame[]> DetectCreditBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects key frames in a media file within a time range asynchronously.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="mode">Analysis mode, used to correctly key the cache entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of absolute media timestamps of key frames.</returns>
    Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default);
}
