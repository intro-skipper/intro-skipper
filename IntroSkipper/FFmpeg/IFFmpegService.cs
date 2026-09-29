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
    /// Scans the keyframes from the credits start to the end of the file: one page per keyframe,
    /// with its black percentage and, inside the credits window, its luma percentiles and saturation.
    /// </summary>
    /// <remarks>
    /// One keyframe decode writes two cache rows, the black-frame row and the visuals row. This reads
    /// each row, or decodes it on a miss, and pairs them into pages, so no caller joins them. A
    /// page's time is its black-frame time. A page has no visual when it lies past the credits
    /// window, when no visual lies within 10 ms of its row, or when the ffmpeg check found the
    /// signalstats filter missing.
    /// </remarks>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>A task that returns one page per keyframe, in time order, relative to the credits fingerprint start.</returns>
    Task<KeyframePage[]> ScanKeyframesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default);

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
    /// Not cached: the window is tens of megabytes, so the caller caches what it derives from it.
    /// The frames stay on the source's own 8-bit scale, 16 to 235 on a limited-range source and 0
    /// to 255 on a full-range one, as the keyframe scan's visuals read the same frames, so a level
    /// from that scan compares directly.
    /// Standard output is capped at 64 MiB.
    /// </remarks>
    /// <param name="episode">Media file to decode.</param>
    /// <param name="window">Absolute media time range to decode. Its start must be a keyframe from the keyframe scan: the decode seeks there, so a demuxer that seeks by timestamp, such as MPEG-TS, decodes from that keyframe rather than the next one, and the frame times fall on the scan's timeline.</param>
    /// <param name="width">Frame width to scale to; the height follows the aspect ratio.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>The frames with their times, or <see langword="null"/> when the decode cannot be trusted: ffmpeg failed to run or exited nonzero, its output crossed the byte cap, or the frame and timestamp counts disagree.</returns>
    Task<LumaWindow?> DecodeLumaWindowAsync(QueuedEpisode episode, TimeRange window, int width, CancellationToken cancellationToken = default);

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
