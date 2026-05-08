// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2022 nyanmisaka
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Runs FFmpeg-based media detection operations with integrated caching.
/// </summary>
public sealed partial class MediaDetectionService : IMediaDetectionService
{
    private readonly IFFmpegRunner _runner;
    private readonly IDetectionCacheService _cacheService;
    private readonly IFFmpegOptionsProvider _options;
    private readonly ILogger<MediaDetectionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDetectionService"/> class.
    /// </summary>
    /// <param name="runner">FFmpeg process runner.</param>
    /// <param name="cacheService">Detection cache service.</param>
    /// <param name="options">FFmpeg options provider.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runner"/>, <paramref name="cacheService"/>, <paramref name="options"/>, or <paramref name="logger"/> is <see langword="null"/>.</exception>
    public MediaDetectionService(
        IFFmpegRunner runner,
        IDetectionCacheService cacheService,
        IFFmpegOptionsProvider options,
        ILogger<MediaDetectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(cacheService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _cacheService = cacheService;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken)
    {
        var (start, end) = episode.GetFingerprintRange(mode);
        var key = new DetectionCacheKey(episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end);

        var cached = await _cacheService.LoadCachedFingerprintAsync(episode, mode, start, end, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            LogFingerprintCacheHit(_logger, episode.Path);
            return cached;
        }

        LogFingerprinting(_logger, start, end, episode.Path, episode.EpisodeId);
        var processResult = await _runner.RunAsync(BuildArgs(), stderr: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        var fingerprint = ParseChromaprintBytes(processResult.Output.AsSpan(), episode.Path);
        await _cacheService.WriteJsonCacheAsync(key, fingerprint, cancellationToken).ConfigureAwait(false);
        return fingerprint;

        string[] BuildArgs() =>
        [
            "-ss", start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", (end - start).ToString(CultureInfo.InvariantCulture),
            "-ac", "2",
            "-f", "chromaprint",
            "-fp_format", "raw",
            "-",
        ];
    }

    /// <inheritdoc />
    public async Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken)
    {
        LogDetectingSilence(_logger, episode.Path, range.Start, range.End, episode.EpisodeId);

        var key = new DetectionCacheKey(episode.EpisodeId, mode, CacheEntryType.Silence, range.Start, range.End);

        /* Each match will have a type (either "start" or "end") and a timecode (a double).
         *
         * Sample output:
         * [silencedetect @ 0x000000000000] silence_start: 12.34
         * [silencedetect @ 0x000000000000] silence_end: 56.123 | silence_duration: 43.783
        */
        return await RunCachedDetectionAsync(
            key,
            DetectionCacheKind.Silence,
            raw => FFmpegOutputParser.ParseSilenceRaw(raw, range.Start),
            BuildArgs,
            stderr: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string[] BuildArgs()
        {
            // -vn, -sn, -dn: ignore video, subtitle, and data tracks
            var noise = _options.SilenceDetectionMaximumNoise.ToString(CultureInfo.InvariantCulture);
            return
            [
                "-vn", "-sn", "-dn",
                "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
                "-i", episode.Path,
                "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
                "-af", $"silencedetect=noise={noise}dB:duration=0.1",
                "-f", "null", "-",
            ];
        }
    }

    /// <inheritdoc />
    public async Task<BlackFrame[]> DetectBlackFramesAsync(
        QueuedEpisode episode,
        TimeRange range,
        int minimum,
        int threshold,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        var key = new DetectionCacheKey(episode.EpisodeId, mode, CacheEntryType.BlackFrame, range.Start, range.End);
        var allFrames = await RunCachedDetectionAsync(
            key,
            DetectionCacheKind.BlackFrameRange,
            static raw => FFmpegOutputParser.ParseBlackFrames(raw),
            BuildArgs,
            stderr: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return [.. allFrames.Where(bf => bf.Percentage >= minimum)];

        string[] BuildArgs() =>
        [
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
            "-an", "-dn", "-sn",
            "-vf", $"blackframe=amount=50:threshold={threshold}",
            "-f", "null", "-",
        ];
    }

    /// <inheritdoc />
    public async Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var key = new DetectionCacheKey(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackFrame, episode.CreditsFingerprintStart, 0);
        return await RunCachedDetectionAsync(
            key,
            DetectionCacheKind.BlackFrameAlt,
            static raw => FFmpegOutputParser.ParseBlackFrames(raw),
            BuildArgs,
            stderr: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string[] BuildArgs() =>
        [
            "-skip_frame", "nokey",
            "-ss", episode.CreditsFingerprintStart.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-an", "-dn", "-sn",
            "-vf", $"blackframe=amount=0:threshold={threshold}",
            "-f", "null", "-",
        ];
    }

    /// <inheritdoc />
    public async Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken)
    {
        var key = new DetectionCacheKey(episode.EpisodeId, mode, CacheEntryType.Keyframe, range.Start, range.End);
        return await RunCachedDetectionAsync(
            key,
            DetectionCacheKind.Keyframe,
            raw => FFmpegOutputParser.ParseKeyFramesRaw(raw, range.Start, _logger),
            BuildArgs,
            stderr: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string[] BuildArgs() =>
        [
            "-skip_frame", "nokey",
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
            "-an", "-dn", "-sn",
            "-vf", "showinfo",
            "-f", "null", "-",
        ];
    }

    private async Task<T[]> RunCachedDetectionAsync<T>(
        DetectionCacheKey key,
        DetectionCacheKind cacheKind,
        Func<string, T[]> parseRawOutput,
        Func<IReadOnlyList<string>> buildArgs,
        bool stderr,
        CancellationToken cancellationToken)
    {
        var cached = await _cacheService.TryReadOrMigrateCacheAsync(key, cacheKind, parseRawOutput, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var processResult = await _runner.RunAsync(buildArgs(), stderr, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = parseRawOutput(DecodeOutput(processResult));
        await _cacheService.WriteJsonCacheAsync(key, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static string DecodeOutput(FFmpegProcessResult result)
        => Encoding.UTF8.GetString(result.Output);

    private uint[] ParseChromaprintBytes(ReadOnlySpan<byte> rawPoints, string path)
    {
        // Returns all fingerprint points as raw 32-bit unsigned integers (little endian).
        if (rawPoints.Length == 0 || rawPoints.Length % 4 != 0)
        {
            LogChromaprintReturnedPoints(_logger, rawPoints.Length, path);
            throw new FingerprintException("chromaprint output for \"" + path + "\" was malformed");
        }

        var results = new uint[rawPoints.Length / 4];
        for (var i = 0; i < results.Length; i++)
        {
            results[i] = BitConverter.ToUInt32(rawPoints.Slice(i * 4, 4));
        }

        return results;
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Detecting silence in \"{File}\" (range {Start}-{End}, id {Id})")]
    private static partial void LogDetectingSilence(ILogger logger, string file, double start, double end, Guid id);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Fingerprint cache hit on {File}")]
    private static partial void LogFingerprintCacheHit(ILogger logger, string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fingerprinting [{Start}, {End}] from \"{File}\" (id {Id})")]
    private static partial void LogFingerprinting(ILogger logger, double start, double end, string file, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chromaprint returned {Count} points for \"{Path}\"")]
    private static partial void LogChromaprintReturnedPoints(ILogger logger, int count, string path);
}
