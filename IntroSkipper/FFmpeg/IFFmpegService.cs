// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
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
    /// <param name="cancellationToken">Cancels this caller's wait for the shared probe. The probe
    /// itself is never canceled by a caller: it runs on a bounded service-owned lifetime, so an
    /// unresponsive ffmpeg fails the attempt and the next call retries.</param>
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
    /// Finds the black level of every keyframe from the credits start to the end of the file.
    /// </summary>
    /// <remarks>
    /// A cache miss is one keyframe scan: it also caches the keyframe visuals of the credits
    /// window, so a following <see cref="DetectKeyframeVisualsAsync"/> for the same episode
    /// reads that row instead of decoding again.
    /// </remarks>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>A task that returns the black level of each keyframe.</returns>
    Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default);

    /// <summary>
    /// Collects per-keyframe visual statistics (luma percentiles and saturation) for the credits fingerprint range.
    /// </summary>
    /// <remarks>
    /// Normally served from the row the keyframe scan in <see cref="DetectBlackFramesAsync(QueuedEpisode, int, CancellationToken)"/>
    /// wrote. Decodes on its own only for an episode whose black-frame row predates that shared
    /// write, or when caching is off. Empty when the ffmpeg check found the visuals filters missing.
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
    /// Decodes every frame of a window to its luma plane at a small width, for the credits lead-in probe.
    /// </summary>
    /// <remarks>
    /// Not cached: every analysis that nominates a lead-in decodes its window again. The frames stay
    /// on the source's own 8-bit scale, 16 to 235 on a limited-range source and 0 to 255 on a
    /// full-range one, as the keyframe scan's visuals read the same frames, so a level from that
    /// scan compares directly.
    /// Standard output is capped at 64 MiB.
    /// </remarks>
    /// <param name="episode">Media file to decode.</param>
    /// <param name="window">Absolute media time range to decode.</param>
    /// <param name="keyframe">A keyframe from the keyframe scan at or before the window start, in media time. The decode seeks there and drops the frames before the window, so a demuxer that seeks by timestamp, such as MPEG-TS, still decodes the window from its start, and the frame times fall on the scan's timeline.</param>
    /// <param name="width">Frame width to scale to; the height follows the aspect ratio.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>The frames with their times, or <see langword="null"/> when the decode cannot be trusted: ffmpeg failed to run or exited nonzero, its output crossed the byte cap, or the frame and timestamp counts disagree.</returns>
    Task<LumaWindow?> DecodeLumaWindowAsync(QueuedEpisode episode, TimeRange window, double keyframe, int width, CancellationToken cancellationToken = default);

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
