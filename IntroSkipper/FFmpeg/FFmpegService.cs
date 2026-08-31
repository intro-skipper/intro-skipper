// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2022 nyanmisaka
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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

    // Bounds one shared version probe. Generous: the probe is four fast ffmpeg info queries,
    // each capped at 2 s of process-exit wait (see CheckFFmpegRequirementAsync), so ~8 s covers
    // a healthy run — but the output drain is awaited before that cap applies, and the probe
    // deliberately ignores caller tokens, so without this lifetime a hung ffmpeg would wedge
    // the gate until a plugin reload.
    private static readonly TimeSpan DefaultVersionProbeTimeout = TimeSpan.FromMinutes(2);

    private readonly ILogger<FFmpegService> _logger = logger;
    private readonly IDetectionCacheService _cacheService = cacheService;
    private readonly Func<CancellationToken, Task<bool>>? _versionProbe;
    private readonly TimeSpan _versionProbeTimeout = DefaultVersionProbeTimeout;

    // Deliberately NOT RetryableInitializationGate: that gate replaces an attempt only
    // when it throws, but this one must also reset on a successful probe whose verdict
    // is false (an incompatible ffmpeg is an expected, re-queryable state), keep success
    // sticky for the service lifetime, and publish a false verdict and its reset
    // atomically so the next caller is guaranteed a fresh probe. A Lazy-based attempt
    // can express none of that.
    private readonly Lock _versionProbeLock = new();

    // Published under _versionProbeLock by the probe attempt whose verdict the gate's waiters
    // observe (an abandoned timed-out probe cannot write it) and read concurrently by the
    // support bundle endpoint, so each run publishes a new immutable snapshot.
    private volatile FFmpegCheckResult _checkResult = FFmpegCheckResult.NotRun;

    private Task<bool>? _versionProbeTask;
    private volatile bool _versionProbeSucceeded;

    internal FFmpegService(
        ILogger<FFmpegService> logger,
        IDetectionCacheService cacheService,
        Func<CancellationToken, Task<bool>> versionProbe,
        TimeSpan? versionProbeTimeout = null)
        : this(logger, cacheService)
    {
        _versionProbe = versionProbe;
        _versionProbeTimeout = versionProbeTimeout ?? DefaultVersionProbeTimeout;
    }

    /// <inheritdoc/>
    public async Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_versionProbeSucceeded)
        {
            return true;
        }

        TaskCompletionSource<bool>? probeCompletion = null;
        Task<bool> probeTask;
        lock (_versionProbeLock)
        {
            if (_versionProbeSucceeded)
            {
                return true;
            }

            if (_versionProbeTask is null)
            {
                probeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _versionProbeTask = probeCompletion.Task;
            }

            probeTask = _versionProbeTask;
        }

        if (probeCompletion is not null)
        {
            _ = RunVersionProbeAsync(probeCompletion);
        }

        return await probeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunVersionProbeAsync(TaskCompletionSource<bool> completion)
    {
        // The probe is shared, so no single caller's token may cancel it; a
        // service-owned lifetime bounds it instead. WaitAsync keeps the gate safe
        // even against a probe that ignores its token: the attempt fails, the gate
        // resets, the next call retries, and the abandoned probe's eventual result
        // is discarded.
        using var probeLifetime = new CancellationTokenSource(_versionProbeTimeout);
        try
        {
            bool valid;
            FFmpegCheckResult? checkResult = null;
            if (_versionProbe is not null)
            {
                valid = await _versionProbe(probeLifetime.Token)
                    .WaitAsync(probeLifetime.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                (valid, checkResult) = await ProbeFFmpegVersionAsync(probeLifetime.Token)
                    .WaitAsync(probeLifetime.Token)
                    .ConfigureAwait(false);
            }

            lock (_versionProbeLock)
            {
                // The verdict's side effects are published only by the attempt the gate's
                // waiters actually observe: an abandoned probe that outran its lifetime
                // completes into a discarded task and can no longer overwrite the check
                // result or warning flag of a newer attempt.
                if (checkResult is not null)
                {
                    _checkResult = checkResult;
                    if (!valid)
                    {
                        WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                    }
                }

                if (valid)
                {
                    _versionProbeSucceeded = true;
                }

                completion.SetResult(valid);
                if (!valid)
                {
                    _versionProbeTask = null;
                }
            }
        }
        catch (OperationCanceledException) when (probeLifetime.IsCancellationRequested)
        {
            // A probe that outran its service-owned lifetime is a failed attempt, not a
            // cancellation of its waiters: CheckFFmpegVersionAsync documents false on
            // any error, and callers such as Entrypoint.StartAsync await it unguarded.
            // Exception propagation stays reserved for unexpected probe failures.
            LogFfmpegVersionProbeTimedOut(_logger, _versionProbeTimeout);

            // The abandoned probe finishes in the background and its result is discarded; an
            // unobserved fault there is inert (since .NET Core it cannot tear down the process).
            // This attempt is the one the gate's waiters observe, so it also publishes the
            // verdict's side effects: without them the support bundle would keep reporting a
            // stale (possibly "okay") check while every caller receives false, and the
            // dashboard would show no ffmpeg warning at all.
            lock (_versionProbeLock)
            {
                _checkResult = new FFmpegCheckResult("timed_out", []);
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                completion.SetResult(false);
                _versionProbeTask = null;
            }
        }
        catch (Exception ex)
        {
            lock (_versionProbeLock)
            {
                completion.SetException(ex);
                _versionProbeTask = null;
            }
        }
    }

    private async Task<(bool Valid, FFmpegCheckResult Result)> ProbeFFmpegVersionAsync(CancellationToken cancellationToken)
    {
        var outputs = new List<FFmpegCheckOutput>();
        try
        {
            // Always log ffmpeg's version information.
            if (!await CheckFFmpegRequirementAsync(
                "-version",
                "ffmpeg",
                "version",
                "Unknown error with FFmpeg version",
                outputs,
                cancellationToken).ConfigureAwait(false))
            {
                return Fail("unknown_error");
            }

            // First, validate that the installed version of ffmpeg supports chromaprint at all.
            if (!await CheckFFmpegRequirementAsync(
                "-muxers",
                "chromaprint",
                "muxer list",
                "The installed version of ffmpeg does not support chromaprint",
                outputs,
                cancellationToken).ConfigureAwait(false))
            {
                return Fail("chromaprint_not_supported");
            }

            // Second, validate that the Chromaprint muxer understands the "-fp_format raw" option.
            if (!await CheckFFmpegRequirementAsync(
                "-h muxer=chromaprint",
                "binary raw fingerprint",
                "chromaprint options",
                "The installed version of ffmpeg does not support raw binary fingerprints",
                outputs,
                cancellationToken).ConfigureAwait(false))
            {
                return Fail("fp_format_not_supported");
            }

            // Third, validate that ffmpeg supports all of the required silencedetect options.
            if (!await CheckFFmpegRequirementAsync(
                "-h filter=silencedetect",
                "noise tolerance",
                "silencedetect options",
                "The installed version of ffmpeg does not support the silencedetect filter",
                outputs,
                cancellationToken).ConfigureAwait(false))
            {
                return Fail("silencedetect_not_supported");
            }

            LogFfmpegVersionValid(_logger);

            return (true, new FFmpegCheckResult("okay", [.. outputs]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFfmpegVersionCheckFailed(_logger, ex);
            return Fail("unknown_error");
        }

        (bool Valid, FFmpegCheckResult Result) Fail(string status)
            => (false, new FFmpegCheckResult(status, [.. outputs]));
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

        // Recap scans report every frame (amount=0) so adaptive threshold normalization can
        // observe the content's full darkness distribution; other modes keep the amount=50
        // superset that existing cache rows and their callers' post-filters rely on.
        var amount = mode == AnalysisMode.Recap ? 0 : 50;
        var args = new List<string>
        {
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
            "-an", "-dn", "-sn",
            "-vf", $"blackframe=amount={amount}:threshold={threshold}",
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
    public async Task<KeyframeVisual[]> DetectKeyframeVisualsAsync(QueuedEpisode episode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        cancellationToken.ThrowIfCancellationRequested();

        // Bound the scan to the configured credits window; FindCreditRange selects the latest
        // qualifying low-entropy run, so scanning past CreditsFingerprintEnd to EOF could otherwise
        // pick up a muted tail (e.g. trailing video after the probed audio duration) as credits.
        var (start, end) = episode.GetFingerprintRange(AnalysisMode.Credits);
        var range = new TimeRange(start, end);

        if (_cacheService.TryRead(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.KeyframeVisual, range.Start, range.End, out KeyframeVisual[] cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }

        // Decode the same keyframes as the black-frame scan, emitting luma histogram entropy and mean
        // saturation per keyframe so credits rendered on a near-uniform low-saturation card (which the black-frame
        // scan is blind to) can be recognised by their near-uniform, low-entropy background.
        // format=yuv420p pins both signals to the 8-bit scale the entropy/saturation thresholds are
        // tuned for, so 10-bit/HDR sources (where signalstats SATAVG is reported ~4x higher) classify
        // consistently rather than missing muted cards.
        var args = new List<string>
        {
            "-skip_frame", "nokey",
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
            "-an", "-dn", "-sn",
            "-vf", "format=yuv420p,entropy,signalstats,metadata=print",
            "-f", "null", "-",
        };

        var raw = Encoding.UTF8.GetString(await GetOutputAsync(args, true, cancellationToken: cancellationToken).ConfigureAwait(false));

        // -to does not reliably bound a -skip_frame nokey scan: FFmpeg still emits keyframes past the
        // requested duration. Clip parsed visuals to the window before caching (times are relative to
        // the -ss seek, so an in-window frame falls within [0, range.Duration]); otherwise
        // FindCreditRange could select a low-entropy run past CreditsFingerprintEnd and persist credits
        // outside the configured scan window.
        var visuals = FFmpegOutputParser.ParseKeyframeVisuals(raw)
            .Where(v => v.Time >= 0 && v.Time <= range.Duration)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        _cacheService.Write(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.KeyframeVisual, range.Start, range.End, visuals);

        return visuals;
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
        var minimumDuration = BlackInterval.MinimumDetectionDuration.ToString(CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-skip_frame", "noref",
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
            : [.. rangeIntervals.Select(interval => new BlackInterval(interval.Start + offset, interval.End + offset))];
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
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            LogAudioDurationProbeFailed(_logger, ex, filePath);
        }

        return null;
    }

    /// <inheritdoc/>
    public FFmpegCheckResult GetCheckResult() => _checkResult;

    /// <summary>
    /// Run an FFmpeg command with the provided arguments and validate that the output contains
    /// the provided string.
    /// </summary>
    /// <param name="arguments">Arguments to pass to FFmpeg.</param>
    /// <param name="mustContain">String that the output must contain. Case-insensitive.</param>
    /// <param name="bundleName">Support bundle name to report FFmpeg's output under.</param>
    /// <param name="errorMessage">Error message to log if this requirement is not met.</param>
    /// <param name="outputs">Per-run list that receives the captured output, whether or not the requirement is met.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>true on success, false on error.</returns>
    private async Task<bool> CheckFFmpegRequirementAsync(
        string arguments,
        string mustContain,
        string bundleName,
        string errorMessage,
        List<FFmpegCheckOutput> outputs,
        CancellationToken cancellationToken)
    {
        LogCheckingRequirement(_logger, arguments);

        var output = Encoding.UTF8.GetString(await GetOutputAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), false, 2000, cancellationToken).ConfigureAwait(false));
        LogFfmpegOutput(_logger, arguments, output);

        outputs.Add(new FFmpegCheckOutput(bundleName, output));

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
            argument.Contains("metadata=print", StringComparison.OrdinalIgnoreCase) ||
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

    internal async Task<byte[]> GetProcessOutputAsync(
        string processPath,
        IReadOnlyList<string> args,
        bool stderr = false,
        int timeout = 60 * 1000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = new ProcessStartInfo(processPath)
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            ErrorDialog = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Starting ffmpeg with the following arguments: {Arguments}", string.Join(" ", info.ArgumentList));
        }

        using var process = new Process { StartInfo = info };
        process.Start();

        try
        {
            try
            {
                process.PriorityClass = Plugin.Instance?.Configuration.ProcessPriority ?? ProcessPriorityClass.BelowNormal;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                LogFfmpegPriorityNotModified(_logger, e.Message);
            }

            using var ms = new MemoryStream();
            // Draining must not use the caller token: on cancellation or timeout the process is
            // killed first, then its remaining output is drained so the pipes cannot deadlock.
            var stdoutTask = DrainAsync(process.StandardOutput.BaseStream, stderr ? null : ms, CancellationToken.None);
            var stderrTask = DrainAsync(process.StandardError.BaseStream, stderr ? ms : null, CancellationToken.None);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                KillProcessTree(process);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                LogFfmpegExitTimeout(_logger, timeout);
                KillProcessTree(process);
                timedOut = true;
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (timedOut)
            {
                throw new TimeoutException($"ffmpeg process was killed after not exiting within {timeout}ms");
            }

            return ms.ToArray();
        }
        finally
        {
            KillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task DrainAsync(Stream stream, Stream? destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (destination is not null)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException ex)
        {
            LogFfmpegProcessAlreadyGone(_logger, ex);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or AggregateException)
        {
            // Kill(entireProcessTree: true) reports partial failures as an AggregateException, and
            // this runs from a cancellation-token callback on a timer thread, where an escaping
            // exception would be unhandled.
            LogFfmpegKillFailed(_logger, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            LogFfmpegKillNotSupported(_logger, ex.Message);
        }
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

    private async Task<AudioStreamSelection?> FindAudioStreamSelectionAsync(
        string filePath,
        string preferredLanguage,
        bool preferMostChannels,
        CancellationToken cancellationToken)
    {
        var hasLanguagePreference = !string.IsNullOrWhiteSpace(preferredLanguage);
        if (!hasLanguagePreference && preferMostChannels)
        {
            // No probe or explicit map is needed to preserve FFmpeg's default selection: most channels, then lowest index.
            return new AudioStreamSelection(null, "policy=most-channels", true);
        }

        try
        {
            var args = new List<string>
            {
                "-v", "error",
                "-select_streams", "a",
                "-show_entries", "stream=index,channels:stream_tags=language",
                "-of", "json",
                filePath,
            };

            var output = Encoding.UTF8.GetString(await GetProcessOutputAsync(
                GetFFprobePath(),
                args,
                stderr: false,
                timeout: 10 * 1000,
                cancellationToken: cancellationToken).ConfigureAwait(false));

            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("streams", out var streams))
            {
                return null;
            }

            var audioStreams = new List<(int Index, int Channels, string? Language)>();
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("index", out var index) && index.TryGetInt32(out var streamIndex))
                {
                    var channels = stream.TryGetProperty("channels", out var channelsElement) &&
                        channelsElement.TryGetInt32(out var channelCount)
                        ? channelCount
                        : 0;
                    var language = stream.TryGetProperty("tags", out var tags) &&
                        tags.TryGetProperty("language", out var languageElement)
                        ? languageElement.GetString()?.Trim()
                        : null;

                    audioStreams.Add((streamIndex, channels, language));
                }
            }

            if (audioStreams.Count == 0)
            {
                return null;
            }

            var defaultStream = SelectAudioStream(audioStreams, preferMostChannels: true);
            var candidates = hasLanguagePreference
                ? audioStreams.Where(stream => string.Equals(stream.Language, preferredLanguage, StringComparison.OrdinalIgnoreCase)).ToList()
                : audioStreams;

            if (candidates.Count == 0)
            {
                // An unmatched language preference falls back to all audio streams using the configured policy.
                candidates = audioStreams;
            }

            var selectedStream = SelectAudioStream(candidates, preferMostChannels);
            var selectsDefaultMostStream = selectedStream.Index == defaultStream.Index;
            var cacheVariant = selectsDefaultMostStream
                ? "policy=most-channels"
                : FormattableString.Invariant($"stream-index={selectedStream.Index}");

            // Legacy rows were fingerprinted from FFmpeg's default stream (most channels, then
            // lowest index), so they are only reusable when that is still the effective stream.
            return new AudioStreamSelection(
                preferMostChannels && selectsDefaultMostStream ? null : selectedStream.Index,
                cacheVariant,
                selectsDefaultMostStream);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            LogPreferredAudioLanguageProbeFailed(_logger, ex, filePath, preferredLanguage);
        }

        return null;
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

        var configuration = Plugin.Instance?.Configuration;
        var preferredLanguage = AudioLanguageHelper.Normalize(configuration?.PreferredAudioLanguage);
        var streamSelection = await FindAudioStreamSelectionAsync(
            episode.Path,
            preferredLanguage,
            configuration?.PreferAudioStreamWithMostChannels ?? true,
            cancellationToken).ConfigureAwait(false);
        var cacheVariant = streamSelection?.CacheVariant;
        var legacyConfigHash = streamSelection?.LegacyDefaultCompatible == true
            ? ConfigHasher.LegacyChromaprintCacheWithoutLanguage(configuration ?? new(), mode)
            : null;

        // Resolve the stream before reading the cache so a language preference can reuse a fingerprint
        // generated with the same effective stream under the default selection.
        if (LoadCachedFingerprint(episode, mode, start, end, cacheVariant, legacyConfigHash, out uint[] cachedFingerprint))
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
        };

        if (streamSelection?.StreamIndex is int streamIndex)
        {
            args.Add("-map");
            args.Add($"0:{streamIndex}?");
        }

        args.AddRange(
        [
            "-ac", "2",
            "-f", "chromaprint",
            "-fp_format", "raw",
            "-",
        ]);

        // Returns all fingerprint points as raw 32-bit unsigned integers (little endian).
        byte[] rawPoints;
        try
        {
            rawPoints = await GetOutputAsync(args, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new FingerprintException($"chromaprint fingerprinting of \"{episode.Path}\" timed out", ex);
        }

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
        _cacheService.Write(episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end, [.. results], cacheVariant);

        return [.. results];
    }

    /// <summary>
    /// Tries to load an episode's fingerprint from cache. If caching is not enabled, calling this function is a no-op.
    /// </summary>
    /// <param name="episode">Episode to try to load from cache.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="start">Start time (in seconds) used when the fingerprint was cached.</param>
    /// <param name="end">End time (in seconds) used when the fingerprint was cached.</param>
    /// <param name="cacheVariant">Effective audio stream selection identity.</param>
    /// <param name="legacyConfigHash">Legacy no-language hash accepted when the selected stream is unchanged.</param>
    /// <param name="fingerprint">Array to store the fingerprint in.</param>
    /// <returns><c>true</c> if the episode was successfully loaded from cache; otherwise <c>false</c>.</returns>
    private bool LoadCachedFingerprint(
        QueuedEpisode episode,
        AnalysisMode mode,
        double start,
        double end,
        string? cacheVariant,
        string? legacyConfigHash,
        out uint[] fingerprint)
    {
        fingerprint = [];
        return _cacheService.TryRead(
            episode.EpisodeId,
            mode,
            CacheEntryType.Chromaprint,
            start,
            end,
            out fingerprint,
            cacheVariant,
            legacyConfigHash);
    }

    private static (int Index, int Channels, string? Language) SelectAudioStream(
        IReadOnlyList<(int Index, int Channels, string? Language)> streams,
        bool preferMostChannels)
        => preferMostChannels
            ? streams.OrderByDescending(stream => stream.Channels).ThenBy(stream => stream.Index).First()
            : streams.OrderBy(stream => stream.Index).First();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Installed version of ffmpeg meets fingerprinting requirements")]
    private static partial void LogFfmpegVersionValid(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while checking the installed FFmpeg version")]
    private static partial void LogFfmpegVersionCheckFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "FFmpeg version check did not finish within {Timeout}; treating the installed FFmpeg as invalid until a later check succeeds")]
    private static partial void LogFfmpegVersionProbeTimedOut(ILogger logger, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ffmpeg priority could not be modified. {Message}")]
    private static partial void LogFfmpegPriorityNotModified(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ffmpeg did not exit within {TimeoutMs}ms; killing process")]
    private static partial void LogFfmpegExitTimeout(ILogger logger, int timeoutMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to kill ffmpeg process tree: {Message}")]
    private static partial void LogFfmpegKillFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killing the ffmpeg process tree is not supported on this platform: {Message}")]
    private static partial void LogFfmpegKillNotSupported(ILogger logger, string message);

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to probe preferred audio language {Language} for {File}; using FFmpeg's default audio stream selection")]
    private static partial void LogPreferredAudioLanguageProbeFailed(ILogger logger, Exception ex, string file, string language);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ffmpeg process already gone while killing process tree")]
    private static partial void LogFfmpegProcessAlreadyGone(ILogger logger, Exception ex);

    private sealed record AudioStreamSelection(int? StreamIndex, string CacheVariant, bool LegacyDefaultCompatible);
}
