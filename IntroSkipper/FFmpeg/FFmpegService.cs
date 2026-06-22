// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2022 nyanmisaka
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides FFmpeg-based media analysis operations.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FFmpegService"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
/// <param name="cacheService">The detection cache service.</param>
public sealed partial class FFmpegService(
    ILogger<FFmpegService> logger,
    IDetectionCacheService cacheService) : IFFmpegService
{
    private const double LimitedRangeLumaMinimum = 16.0;
    private const double LimitedRangeLumaRange = 219.0;

    private readonly ILogger<FFmpegService> _logger = logger;
    private readonly IDetectionCacheService _cacheService = cacheService;
    private readonly FFmpegProcess _process = new(logger);

    // Written by CheckFFmpegVersionAsync (serialized via ScheduledTaskSemaphore) but read concurrently
    // by the support bundle endpoint, so a concurrent dictionary is required.
    private readonly ConcurrentDictionary<string, string> _chromaprintLogs = new();

    /// <inheritdoc/>
    public async Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Always log ffmpeg's version information.
            if (!await CheckFFmpegRequirementAsync(
                "-version",
                "ffmpeg",
                "version",
                "Unknown error with FFmpeg version",
                cancellationToken).ConfigureAwait(false))
            {
                _chromaprintLogs["error"] = "unknown_error";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            // First, validate that the installed version of ffmpeg supports chromaprint at all.
            if (!await CheckFFmpegRequirementAsync(
                "-muxers",
                "chromaprint",
                "muxer list",
                "The installed version of ffmpeg does not support chromaprint",
                cancellationToken).ConfigureAwait(false))
            {
                _chromaprintLogs["error"] = "chromaprint_not_supported";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            // Second, validate that the Chromaprint muxer understands the "-fp_format raw" option.
            if (!await CheckFFmpegRequirementAsync(
                "-h muxer=chromaprint",
                "binary raw fingerprint",
                "chromaprint options",
                "The installed version of ffmpeg does not support raw binary fingerprints",
                cancellationToken).ConfigureAwait(false))
            {
                _chromaprintLogs["error"] = "fp_format_not_supported";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            // Third, validate that ffmpeg supports all of the required silencedetect options.
            if (!await CheckFFmpegRequirementAsync(
                "-h filter=silencedetect",
                "noise tolerance",
                "silencedetect options",
                "The installed version of ffmpeg does not support the silencedetect filter",
                cancellationToken).ConfigureAwait(false))
            {
                _chromaprintLogs["error"] = "silencedetect_not_supported";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            LogFfmpegVersionValid(_logger);

            _chromaprintLogs["error"] = "okay";
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFfmpegVersionCheckFailed(_logger, ex);
            _chromaprintLogs["error"] = "unknown_error";
            WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
            return false;
        }
    }

    /// <inheritdoc/>
    public Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (start, end) = episode.GetFingerprintRange(mode);
        return FingerprintAsync(episode, mode, start, end, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        LogDetectingSilence(_logger, episode.Path, range.Start, range.End, episode.EpisodeId);

        if (_cacheService.TryRead(episode.EpisodeId, mode, CacheEntryType.Silence, range.Start, range.End, out TimeRange[] cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }

        // -vn, -sn, -dn: ignore video, subtitle, and data tracks
        var noise = (Plugin.Instance?.Configuration.SilenceDetectionMaximumNoise ?? -50).ToString(CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-vn", "-sn", "-dn",
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
            "-af", $"silencedetect=noise={noise}dB:duration=0.1",
            "-f", "null", "-",
        };

        /* Each match will have a type (either "start" or "end") and a timecode (a double).
         *
         * Sample output:
         * [silencedetect @ 0x000000000000] silence_start: 12.34
         * [silencedetect @ 0x000000000000] silence_end: 56.123 | silence_duration: 43.783
        */
        var raw = Encoding.UTF8.GetString(await GetOutputAsync(args, true, cancellationToken: cancellationToken).ConfigureAwait(false));
        var result = FFmpegOutputParser.ParseSilence(raw, range.Start);
        cancellationToken.ThrowIfCancellationRequested();
        _cacheService.Write(episode.EpisodeId, mode, CacheEntryType.Silence, range.Start, range.End, result);

        return result;
    }

    /// <inheritdoc/>
    public async Task<BlackFrame[]> DetectBlackFramesAsync(
        QueuedEpisode episode,
        TimeRange range,
        int minimum,
        int threshold,
        AnalysisMode mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_cacheService.TryRead(episode.EpisodeId, mode, CacheEntryType.BlackFrame, range.Start, range.End, out BlackFrame[] cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return [.. cached.Where(bf => bf.Percentage >= minimum)];
        }

        // Seek to the start of the time range and find frames that are at least 50% black.
        var args = new List<string>
        {
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
            "-an", "-dn", "-sn",
            "-vf", $"blackframe=amount=50:threshold={threshold}",
            "-f", "null", "-",
        };

        var raw = Encoding.UTF8.GetString(await GetOutputAsync(args, true, cancellationToken: cancellationToken).ConfigureAwait(false));
        var allFrames = FFmpegOutputParser.ParseBlackFrames(raw);
        cancellationToken.ThrowIfCancellationRequested();
        _cacheService.Write(episode.EpisodeId, mode, CacheEntryType.BlackFrame, range.Start, range.End, allFrames);

        return [.. allFrames.Where(bf => bf.Percentage >= minimum)];
    }

    /// <inheritdoc/>
    public async Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        cancellationToken.ThrowIfCancellationRequested();

        if (_cacheService.TryRead(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackFrame, episode.CreditsFingerprintStart, 0, out BlackFrame[] cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }

        // Seek to the start of the time range and get the black level of each frame.
        var args = new List<string>
        {
            "-skip_frame", "nokey",
            "-ss", episode.CreditsFingerprintStart.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-an", "-dn", "-sn",
            "-vf", $"blackframe=amount=0:threshold={threshold}",
            "-f", "null", "-",
        };

        var raw = Encoding.UTF8.GetString(await GetOutputAsync(args, true, cancellationToken: cancellationToken).ConfigureAwait(false));
        var allFrames = FFmpegOutputParser.ParseBlackFrames(raw);
        cancellationToken.ThrowIfCancellationRequested();
        _cacheService.Write(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackFrame, episode.CreditsFingerprintStart, 0, allFrames);

        return allFrames;
    }

    /// <inheritdoc/>
    public Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, int threshold, int minimum, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var (start, end) = episode.GetFingerprintRange(AnalysisMode.Credits);
        return DetectBlackIntervalsAsync(episode, new TimeRange(start, end), threshold, minimum, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, TimeRange range, int threshold, int minimum, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        cancellationToken.ThrowIfCancellationRequested();

        if (_cacheService.TryRead(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackInterval, range.Start, range.End, out BlackInterval[] cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }

        var pixelThreshold = FormatBlackDetectPixelThreshold(threshold);
        var pictureRatioThreshold = FormatBlackDetectPictureRatioThreshold(minimum);
        var minimumDuration = BlackIntervalConstants.MinimumDuration.ToString(CultureInfo.InvariantCulture);
        // blackdetect measures continuous black duration from the decoded stream, so the decoder must
        // see every frame. Skipping non-reference frames (e.g. -skip_frame noref) would let the filter
        // merge through brief non-black B-frames or miss short black intervals, so it is intentionally
        // not used here.
        var args = new List<string>
        {
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
            "-an", "-dn", "-sn",
            "-vf", $"blackdetect=d={minimumDuration}:pix_th={pixelThreshold}:pic_th={pictureRatioThreshold}",
            "-f", "null", "-",
        };

        var raw = Encoding.UTF8.GetString(await GetOutputAsync(args, true, cancellationToken: cancellationToken).ConfigureAwait(false));
        var rangeIntervals = FFmpegOutputParser.ParseBlackIntervals(raw);
        var offset = range.Start - episode.CreditsFingerprintStart;
        var intervals = offset == 0
            ? rangeIntervals
            : [.. rangeIntervals.Select(interval => new BlackInterval(interval.Start + offset, interval.End + offset, interval.Duration))];
        cancellationToken.ThrowIfCancellationRequested();
        _cacheService.Write(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackInterval, range.Start, range.End, intervals);

        return intervals;
    }

    // blackdetect's pix_th is a fraction of the luma range; internally it derives the absolute cutoff
    // as (16 + pix_th * 219) for limited-range video. We invert that here so the cutoff equals the
    // configured blackframe `threshold` (a raw 0-255 luma value), keeping both filters' pixel-level
    // notion of "black" identical. This assumes limited range (TV swing, 16-235); on full-range
    // sources blackdetect divides by 255 instead, making its cutoff marginally stricter.
    private static string FormatBlackDetectPixelThreshold(int threshold)
    {
        var normalizedThreshold = Math.Clamp((threshold - LimitedRangeLumaMinimum) / LimitedRangeLumaRange, 0, 1);
        return normalizedThreshold.ToString("0.####", CultureInfo.InvariantCulture);
    }

    // pic_th is the fraction of a frame that must be black for blackdetect to treat the frame as black.
    // Tie it to the same `minimum` percentage the keyframe density pass uses so the interval confirmer
    // and the keyframe proposer agree on what counts as a black frame; otherwise blackdetect's default
    // 0.98 would reject text-heavy real credits that the keyframe pass (~0.85) accepts.
    private static string FormatBlackDetectPictureRatioThreshold(int minimum)
    {
        var ratio = Math.Clamp(minimum / 100.0, 0, 1);
        return ratio.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_cacheService.TryRead(episode.EpisodeId, mode, CacheEntryType.Keyframe, range.Start, range.End, out double[] cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }

        var args = new List<string>
        {
            "-skip_frame", "nokey",
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
            "-an", "-dn", "-sn",
            "-vf", "showinfo",
            "-f", "null", "-",
        };

        var raw = Encoding.UTF8.GetString(await GetOutputAsync(args, stderr: true, cancellationToken: cancellationToken).ConfigureAwait(false));
        var result = FFmpegOutputParser.ParseKeyFrames(raw, range.Start, _logger);
        cancellationToken.ThrowIfCancellationRequested();
        _cacheService.Write(episode.EpisodeId, mode, CacheEntryType.Keyframe, range.Start, range.End, result);

        return result;
    }

    /// <inheritdoc/>
    public async Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var ffprobePath = GetFFprobePath();
            var args = new List<string>
            {
                "-v", "error",
                "-select_streams", "a:0",
                "-show_entries", "stream=duration:stream_tags=DURATION",
                "-of", "csv=p=0",
                filePath,
            };

            var output = Encoding.UTF8.GetString(await GetProcessOutputAsync(ffprobePath, args, stderr: false, timeout: 10 * 1000, cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            foreach (var value in output.Split('\n')[0].Split(',').Select(static f => f.Trim()))
            {
                if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (double.TryParse(value, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    return seconds;
                }

                if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration) && duration.TotalSeconds > 0)
                {
                    return duration.TotalSeconds;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            LogAudioDurationProbeFailed(_logger, ex, filePath);
        }

        return null;
    }

    /// <inheritdoc/>
    public string GetChromaprintLogs()
    {
        // Print the FFmpeg detection status at the top.
        // Format: "* FFmpeg: `error`"
        // Append two newlines to separate the bulleted list from the logs
        var logs = new StringBuilder()
            .Append("* FFmpeg: `")
            .Append(_chromaprintLogs.GetValueOrDefault("error", "unknown"))
            .Append("`\n\n");

        // Always include ffmpeg version information
        logs.Append(FormatFFmpegLog("version"));

        // Include feature detection logs to verify no warnings
        foreach (var kvp in _chromaprintLogs.Where(kvp => kvp.Key is not "error" and not "version"))
        {
            logs.Append(FormatFFmpegLog(kvp.Key));
        }

        return logs.ToString();
    }

    /// <summary>
    /// Run an FFmpeg command with the provided arguments and validate that the output contains
    /// the provided string.
    /// </summary>
    /// <param name="arguments">Arguments to pass to FFmpeg.</param>
    /// <param name="mustContain">String that the output must contain. Case-insensitive.</param>
    /// <param name="bundleName">Support bundle key to store FFmpeg's output under.</param>
    /// <param name="errorMessage">Error message to log if this requirement is not met.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>true on success, false on error.</returns>
    private async Task<bool> CheckFFmpegRequirementAsync(
        string arguments,
        string mustContain,
        string bundleName,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        LogCheckingRequirement(_logger, arguments);

        var output = Encoding.UTF8.GetString(await GetOutputAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), false, 2000, cancellationToken).ConfigureAwait(false));
        LogFfmpegOutput(_logger, arguments, output);

        _chromaprintLogs[bundleName] = output;

        if (!output.Contains(mustContain, StringComparison.OrdinalIgnoreCase))
        {
            LogFfmpegRequirementFailed(_logger, errorMessage);
            return false;
        }

        LogFfmpegRequirementMet(_logger, arguments);
        return true;
    }

    /// <summary>
    /// Runs ffmpeg and returns standard output (or error).
    /// </summary>
    /// <param name="args">Arguments to pass to ffmpeg as individual tokens.</param>
    /// <param name="stderr">If standard error should be returned.</param>
    /// <param name="timeout">Timeout (in miliseconds) to wait for ffmpeg to exit.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    private Task<byte[]> GetOutputAsync(
        IReadOnlyList<string> args,
        bool stderr = false,
        int timeout = 60 * 1000,
        CancellationToken cancellationToken = default)
    {
        var logLevel = UsesInfoLogLevel(args) ? "info" : "warning";

        var processArgs = new List<string> { "-hide_banner" };
        if (!IsInfoQuery(args))
        {
            processArgs.Add("-threads");
            processArgs.Add((Plugin.Instance?.Configuration.ProcessThreads ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        processArgs.Add("-loglevel");
        processArgs.Add(logLevel);
        processArgs.AddRange(args);

        return GetProcessOutputAsync(Plugin.Instance?.FFmpegPath ?? "ffmpeg", processArgs, stderr, timeout, cancellationToken);
    }

    private static bool UsesInfoLogLevel(IReadOnlyList<string> args)
    {
        // Detection filters emit their result data at info log level.
        return args.Any(argument =>
            argument.Contains("silencedetect", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("blackframe", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("blackdetect", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("showinfo", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInfoQuery(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return false;
        }

        // Do not add thread count to quick info queries; ffmpeg treats it as a trailing option.
        var firstArg = args[0];
        return firstArg.StartsWith("-version", StringComparison.Ordinal) ||
            firstArg.StartsWith("-muxers", StringComparison.Ordinal) ||
            firstArg.StartsWith("-h", StringComparison.Ordinal);
    }

    private Task<byte[]> GetProcessOutputAsync(
        string processPath,
        IReadOnlyList<string> args,
        bool stderr = false,
        int timeout = 60 * 1000,
        CancellationToken cancellationToken = default)
    {
        return _process.RunAsync(processPath, args, stderr, timeout, Plugin.Instance?.Configuration.ProcessPriority, cancellationToken);
    }

    private static string GetFFprobePath()
    {
        var ffmpegPath = Plugin.Instance?.FFmpegPath ?? "ffmpeg";
        var extension = Path.GetExtension(ffmpegPath);
        var withoutExtension = Path.ChangeExtension(ffmpegPath, null);
        var candidate = withoutExtension + "probe" + extension;
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return Path.Join(Path.GetDirectoryName(ffmpegPath) ?? string.Empty, "ffprobe" + extension);
    }

    /// <summary>
    /// Fingerprint a queued episode.
    /// </summary>
    /// <param name="episode">Queued episode to fingerprint.</param>
    /// <param name="mode">Portion of media file to fingerprint.</param>
    /// <param name="start">Time (in seconds) relative to the start of the file to start fingerprinting from.</param>
    /// <param name="end">Time (in seconds) relative to the start of the file to stop fingerprinting at.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>Numerical fingerprint points.</returns>
    private async Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, double start, double end, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Try to load this episode from cache before running ffmpeg.
        if (LoadCachedFingerprint(episode, mode, start, end, out uint[] cachedFingerprint))
        {
            LogFingerprintCacheHit(_logger, episode.Path);
            cancellationToken.ThrowIfCancellationRequested();
            return cachedFingerprint;
        }

        LogFingerprinting(_logger, start, end, episode.Path, episode.EpisodeId);

        var args = new List<string>
        {
            "-ss", start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", (end - start).ToString(CultureInfo.InvariantCulture),
            "-ac", "2",
            "-f", "chromaprint",
            "-fp_format", "raw",
            "-",
        };

        // Returns all fingerprint points as raw 32-bit unsigned integers (little endian).
        var rawPoints = await GetOutputAsync(args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (rawPoints.Length == 0 || rawPoints.Length % 4 != 0)
        {
            LogChromaprintReturnedPoints(_logger, rawPoints.Length, episode.Path);
            throw new FingerprintException("chromaprint output for \"" + episode.Path + "\" was malformed");
        }

        var results = new List<uint>();
        for (var i = 0; i < rawPoints.Length; i += 4)
        {
            var rawPoint = rawPoints.AsSpan(i, 4);
            results.Add(BitConverter.ToUInt32(rawPoint));
        }

        // Try to cache this fingerprint.
        cancellationToken.ThrowIfCancellationRequested();
        _cacheService.Write(episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end, [.. results]);

        return [.. results];
    }

    /// <summary>
    /// Tries to load an episode's fingerprint from cache. If caching is not enabled, calling this function is a no-op.
    /// </summary>
    /// <param name="episode">Episode to try to load from cache.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="start">Start time (in seconds) used when the fingerprint was cached.</param>
    /// <param name="end">End time (in seconds) used when the fingerprint was cached.</param>
    /// <param name="fingerprint">Array to store the fingerprint in.</param>
    /// <returns><c>true</c> if the episode was successfully loaded from cache; otherwise <c>false</c>.</returns>
    private bool LoadCachedFingerprint(
        QueuedEpisode episode,
        AnalysisMode mode,
        double start,
        double end,
        out uint[] fingerprint)
    {
        fingerprint = [];
        return _cacheService.TryRead(episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end, out fingerprint);
    }

    private string FormatFFmpegLog(string key)
    {
        /* Format:
        * FFmpeg NAME:
        * ```
        * LOGS
        * ```
        */

        var formatted = string.Format(CultureInfo.InvariantCulture, "FFmpeg {0}:\n```\n", key);
        formatted += _chromaprintLogs.GetValueOrDefault(key, "(no output captured)");

        // Ensure the closing triple backtick is on a separate line
        if (!formatted.EndsWith('\n'))
        {
            formatted += "\n";
        }

        formatted += "```\n\n";

        return formatted;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Installed version of ffmpeg meets fingerprinting requirements")]
    private static partial void LogFfmpegVersionValid(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while checking the installed FFmpeg version")]
    private static partial void LogFfmpegVersionCheckFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Detecting silence in \"{File}\" (range {Start}-{End}, id {Id})")]
    private static partial void LogDetectingSilence(ILogger logger, string file, double start, double end, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Checking FFmpeg requirement {Arguments}")]
    private static partial void LogCheckingRequirement(ILogger logger, string arguments);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Output of ffmpeg {Arguments}: {Output}")]
    private static partial void LogFfmpegOutput(ILogger logger, string arguments, string output);

    [LoggerMessage(Level = LogLevel.Error, Message = "{ErrorMessage}")]
    private static partial void LogFfmpegRequirementFailed(ILogger logger, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FFmpeg requirement {Arguments} met")]
    private static partial void LogFfmpegRequirementMet(ILogger logger, string arguments);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Fingerprint cache hit on {File}")]
    private static partial void LogFingerprintCacheHit(ILogger logger, string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fingerprinting [{Start}, {End}] from \"{File}\" (id {Id})")]
    private static partial void LogFingerprinting(ILogger logger, double start, double end, string file, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chromaprint returned {Count} points for \"{Path}\"")]
    private static partial void LogChromaprintReturnedPoints(ILogger logger, int count, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to probe audio duration for {File}")]
    private static partial void LogAudioDurationProbeFailed(ILogger logger, Exception ex, string file);
}
