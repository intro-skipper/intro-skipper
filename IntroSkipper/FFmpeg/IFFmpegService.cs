// SPDX-FileCopyrightText: 2024-2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides FFmpeg-based media analysis operations including fingerprinting,
/// silence detection, black frame detection, and key frame detection.
/// </summary>
public interface IFFmpegService
{
    /// <summary>
    /// Check that the installed version of ffmpeg supports chromaprint. A successful
    /// probe is memoized for the service lifetime; a failed probe is retried on the
    /// next call so a repaired ffmpeg installation is observed without a restart.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel FFmpeg requirement checks.</param>
    /// <returns>A task that returns <see langword="true"/> if a compatible version of ffmpeg is installed, <see langword="false"/> on any error.</returns>
    Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fingerprint a queued episode.
    /// </summary>
    /// <param name="episode">Queued episode to fingerprint.</param>
    /// <param name="mode">Portion of media file to fingerprint.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>A task that returns numerical fingerprint points.</returns>
    Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detect ranges of silence in the provided episode.
    /// </summary>
    /// <param name="episode">Queued episode.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="mode">Analysis mode, used to correctly key the cache entry.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>A task that returns TimeRange objects that are silent in the queued episode.</returns>
    Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the location of all black frames in a media file within a time range.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="minimum">Percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="mode">Analysis mode, used to correctly key the cache entry.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>A task that returns frames that are mostly black.</returns>
    Task<BlackFrame[]> DetectBlackFramesAsync(
        QueuedEpisode episode,
        TimeRange range,
        int minimum,
        int threshold,
        AnalysisMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the location of all black frames in a media file starting at a given time.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>A task that returns frames that are mostly black.</returns>
    Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default);

    /// <summary>
    /// Collects per-keyframe visual statistics (entropy and saturation) for the credits fingerprint range.
    /// </summary>
    /// <remarks>
    /// Emitted from a single keyframe decode (the same shape as the black-frame scan) so non-black
    /// credit detection can run without decoding the video a second time per frame.
    /// </remarks>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>A task that returns per-keyframe visual statistics relative to the credits fingerprint start.</returns>
    Task<KeyframeVisual[]> DetectKeyframeVisualsAsync(QueuedEpisode episode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds continuous black intervals in a bounded credits range.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Absolute media time range to search.</param>
    /// <param name="threshold">Pixel threshold for black interval detection.</param>
    /// <param name="minimum">Minimum percentage of a frame that must be black for it to count as black (blackdetect pic_th); tie this to the keyframe density threshold so both definitions of "black" agree.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>A task that returns continuous black intervals relative to the credits fingerprint start.</returns>
    Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, TimeRange range, int threshold, int minimum, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects key frames in a media file within a time range.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="mode">Analysis mode, used to correctly key the cache entry.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>A task that returns timestamps of key frames.</returns>
    Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes the first audio stream's actual duration with ffprobe.
    /// </summary>
    /// <param name="filePath">Media path.</param>
    /// <param name="cancellationToken">Token used to cancel the ffprobe process.</param>
    /// <returns>A task that returns the audio duration in seconds, or null when unavailable.</returns>
    Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the outcome of the most recent <see cref="CheckFFmpegVersionAsync"/> run for the support bundle.
    /// </summary>
    /// <returns>The status token and the raw output of each probe in check order; <see cref="FFmpegCheckResult.NotRun"/> before the first check.</returns>
    FFmpegCheckResult GetCheckResult();
}
