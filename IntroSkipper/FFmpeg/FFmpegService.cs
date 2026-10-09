// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides FFmpeg-based media analysis operations.
/// </summary>
internal sealed partial class FFmpegService : IFFmpegService
{
    private const double LimitedRangeLumaMinimum = 16.0;
    private const double LimitedRangeLumaRange = 219.0;

    // Per-keyframe luma percentiles and mean saturation. format=yuv420p brings 10-bit sources to
    // 8-bit values and keeps the source's range, so a full-range source spans 0 to 255.
    private const string KeyframeVisualFilters = "format=yuv420p,signalstats,metadata=print";

    // VP9/WebM may ignore -skip_frame nokey; filter expensive scans by packet keyframe.
    private const string KeyframeSelect = "select=eq(key\\,1)";

    // Bytes of each ffmpeg output stream a lead-in probe may hold at once. The probe decodes at a
    // small width, a few megabytes of luma and a line of showinfo per frame, so the cap only stops
    // a runaway decode or a file whose diagnostics never end.
    private const long LumaWindowMaximumBytes = 64L * 1024 * 1024;

    // Subtitle text should be small, but a malformed or malicious episode must not allow FFmpeg's
    // stdout captures to grow without bound across its embedded streams and sidecars.
    private const long SubtitleEpisodeMaximumBytes = 16L * 1024 * 1024;

    // A media container can advertise an unbounded number of subtitle streams; cap ffprobe's
    // JSON response separately from the larger, decoded subtitle-text allowance.
    private const long SubtitleProbeMaximumBytes = 1024L * 1024;

    private static readonly HashSet<string> ImageSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "dvb_subtitle", "dvb_teletext", "dvd_subtitle", "hdmv_pgs_subtitle", "xsub",
    };

    // A keyframe-only decode with frame threads runs one keyframe at a time once keyframes sit
    // further apart than the frame-thread window, as they do in episodes. x265 codes HEVC with
    // wavefronts by default, and slice threads decode a keyframe's wavefront rows on several
    // cores. On a well-formed stream the thread type never changes the decoded pixels.
    private static readonly string[] HevcKeyframeThreads = ["-thread_type", "slice"];

    // HEVC decoder errors of the wavefront path, which slice threads use: it trusts each slice
    // header's entry points, and a stream whose slice segments don't match them decodes as black
    // with exit 0. A slice-threaded scan that logs one is decoded again with frame threads
    // (RunCachedScanAsync). "Independent slice segment missing." is not among them: healthy
    // streams with dependent slices log it on every skipped frame.
    private static readonly string[] SliceStructureErrors = ["entry_point_offset table is corrupted", "WPP ctb addresses are wrong", "Previous slice segment missing"];

    // Generous: the probe is five fast ffmpeg info queries, each capped at 2 s of process-exit
    // wait (see ProbeFFmpegVersionAsync), so ~8 s covers a healthy run, but the output drain
    // is awaited before that cap applies.
    private static readonly TimeSpan DefaultVersionProbeTimeout = TimeSpan.FromMinutes(2);

    // Probed in order; the first unmet requirement decides the check status. The output of every
    // probe that ran is kept for the support bundle under its bundle name.
    private static readonly (string Arguments, string MustContain, string BundleName, string ErrorMessage, string FailureStatus)[] Requirements =
    [
        ("-version", "ffmpeg", "version", "Unknown error with FFmpeg version", "unknown_error"),
        ("-muxers", "chromaprint", "muxer list", "The installed version of ffmpeg does not support chromaprint", "chromaprint_not_supported"),
        ("-h muxer=chromaprint", "binary raw fingerprint", "chromaprint options", "The installed version of ffmpeg does not support raw binary fingerprints", "fp_format_not_supported"),
        ("-h filter=silencedetect", "noise tolerance", "silencedetect options", "The installed version of ffmpeg does not support the silencedetect filter", "silencedetect_not_supported"),
    ];

    // Probed after the requirements. This filter only feeds keyframe visuals, so a build
    // without it keeps every other scan and loses card credits detection.
    private static readonly (string Filter, string BundleName)[] KeyframeVisualFilterProbes =
    [
        ("signalstats", "signalstats options"),
    ];

    private readonly ILogger<FFmpegService> _logger;
    private readonly DetectionCacheService _cacheService;
    private readonly FFmpegProcessRunner _processRunner;
    private readonly FFmpegVersionGate _versionGate;
    private readonly ConditionalWeakTable<QueuedEpisode, Task<string?>> _videoCodecs = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cacheService">The detection cache service.</param>
    public FFmpegService(ILogger<FFmpegService> logger, DetectionCacheService cacheService)
        : this(logger, cacheService, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegService"/> class with a replaced version probe (tests only).
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cacheService">The detection cache service.</param>
    /// <param name="versionProbe">Replaces the ffmpeg version probe; <see langword="null"/> runs the real one.</param>
    /// <param name="versionProbeTimeout">Bounds one probe attempt; <see langword="null"/> uses the default.</param>
    internal FFmpegService(
        ILogger<FFmpegService> logger,
        DetectionCacheService cacheService,
        Func<CancellationToken, Task<bool>>? versionProbe,
        TimeSpan? versionProbeTimeout)
    {
        _logger = logger;
        _cacheService = cacheService;
        _processRunner = new FFmpegProcessRunner(logger);
        _versionGate = new FFmpegVersionGate(
            logger,
            versionProbe is null
                ? ProbeFFmpegVersionAsync
                : async cancellationToken => (await versionProbe(cancellationToken).ConfigureAwait(false), null),
            versionProbeTimeout ?? DefaultVersionProbeTimeout);
    }

    private static string FFmpegPath => Plugin.Instance?.FFmpegPath ?? "ffmpeg";

    /// <inheritdoc/>
    public Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
        => _versionGate.CheckAsync(cancellationToken);

    /// <inheritdoc/>
    public FFmpegCheckResult GetCheckResult() => _versionGate.CheckResult;

    private async Task<(bool Valid, FFmpegCheckResult? Result)> ProbeFFmpegVersionAsync(CancellationToken cancellationToken)
    {
        var outputs = new List<FFmpegCheckOutput>();
        try
        {
            async Task<string> ProbeAsync(string arguments, string bundleName)
            {
                var output = Encoding.UTF8.GetString(await GetOutputAsync(
                    arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    stderr: false,
                    infoQuery: true,
                    timeout: 2000,
                    cancellationToken).ConfigureAwait(false));
                LogFfmpegOutput(_logger, arguments, output);
                outputs.Add(new FFmpegCheckOutput(bundleName, output));
                return output;
            }

            foreach (var (arguments, mustContain, bundleName, errorMessage, failureStatus) in Requirements)
            {
                var output = await ProbeAsync(arguments, bundleName).ConfigureAwait(false);
                if (!output.Contains(mustContain, StringComparison.OrdinalIgnoreCase))
                {
                    LogFfmpegRequirementFailed(_logger, errorMessage);
                    return (false, new FFmpegCheckResult(failureStatus, [.. outputs]));
                }
            }

            var visualsSupported = true;
            foreach (var (filter, bundleName) in KeyframeVisualFilterProbes)
            {
                var output = await ProbeAsync("-h filter=" + filter, bundleName).ConfigureAwait(false);
                if (!output.Contains("Filter " + filter, StringComparison.OrdinalIgnoreCase))
                {
                    LogKeyframeVisualFilterUnsupported(_logger, filter);
                    visualsSupported = false;
                }
            }

            return (true, new FFmpegCheckResult("okay", [.. outputs], visualsSupported));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFfmpegVersionCheckFailed(_logger, ex);
            return (false, new FFmpegCheckResult("unknown_error", [.. outputs]));
        }
    }

    /// <inheritdoc/>
    public Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        // -vn, -sn, -dn: ignore video, subtitle, and data tracks
        var noise = (Plugin.Instance?.Configuration.SilenceDetectionMaximumNoise ?? -50).ToString(CultureInfo.InvariantCulture);
        string[] args =
        [
            "-vn", "-sn", "-dn",
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
            "-af", $"silencedetect=noise={noise}dB:duration=0.1",
            "-f", "null", "-",
        ];

        /* Each match will have a type (either "start" or "end") and a timecode (a double).
         *
         * Sample output:
         * [silencedetect @ 0x000000000000] silence_start: 12.34
         * [silencedetect @ 0x000000000000] silence_end: 56.123 | silence_duration: 43.783
        */
        return RunCachedScanAsync(episode, mode, CacheEntryType.Silence, range.Start, range.End, args, raw => FFmpegOutputParser.ParseSilence(raw, range.Start), cancellationToken);
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
        // Recap scans report every frame (amount=0) so adaptive threshold normalization can
        // observe the content's full darkness distribution; other modes keep the amount=50
        // superset that existing cache rows and their callers' post-filters rely on.
        var amount = mode == AnalysisMode.Recap ? 0 : 50;
        string[] args =
        [
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-i", episode.Path,
            "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
            "-an", "-dn", "-sn",
            "-vf", $"blackframe=amount={amount}:threshold={threshold}",
            "-f", "null", "-",
        ];

        var allFrames = await RunCachedScanAsync(episode, mode, CacheEntryType.BlackFrame, range.Start, range.End, args, FFmpegOutputParser.ParseBlackFrames, cancellationToken).ConfigureAwait(false);
        return [.. allFrames.Where(bf => bf.Percentage >= minimum)];
    }

    /// <inheritdoc/>
    public async Task<KeyframePage[]> ScanKeyframesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
    {
        // The black-frame scan goes first: on a cache miss its decode also writes the visuals row,
        // so the visuals read after it is a cache hit. Run concurrently, a miss would decode twice.
        var rows = await DetectBlackFramesAsync(episode, threshold, cancellationToken).ConfigureAwait(false);
        var visuals = await DetectKeyframeVisualsAsync(episode, cancellationToken).ConfigureAwait(false);
        return KeyframeJoin.Pages(rows, visuals);
    }

    /// <summary>
    /// Finds the black percentage of every keyframe from the credits start to the end of the file.
    /// </summary>
    /// <remarks>
    /// A cache miss is one keyframe scan: it also caches the keyframe visuals of the credits
    /// window, so a following <see cref="DetectKeyframeVisualsAsync"/> for the same episode reads
    /// that row instead of decoding again.
    /// </remarks>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>A task that returns the black percentage of each keyframe.</returns>
    internal async Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
    {
        var (start, end) = episode.GetFingerprintRange(AnalysisMode.Credits);
        var window = new TimeRange(start, end);
        var withVisuals = _versionGate.CheckResult.KeyframeVisualsSupported;

        // The visuals row is keyed by the credits window, as the standalone visuals scan writes
        // it; the black-frame row keeps its end-of-file key.
        return await RunCachedScanAsync(
            episode,
            AnalysisMode.Credits,
            CacheEntryType.BlackFrame,
            start,
            0,
            async () =>
            {
                // One decode feeds separate blackframe and visual filtergraphs: blackframe keeps
                // source negotiation, while signalstats reads 8-bit yuv420p, which keeps the
                // source's range.
                var codec = await GetVideoCodecAsync(episode, cancellationToken).ConfigureAwait(false);
                return
                [
                    .. KeyframeInput(codec, start, episode.Path),
                    .. OutputArgs(KeyframeFilters(codec, $"blackframe=amount=0:threshold={threshold}")),
                    .. withVisuals ? OutputArgs(KeyframeFilters(codec, KeyframeVisualFilters)) : [],
                ];
            },
            raw =>
            {
                if (withVisuals)
                {
                    _cacheService.Write(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.KeyframeVisual, window.Start, window.End, ParseKeyframeVisualsInWindow(raw, window));
                }

                return FFmpegOutputParser.ParseBlackFrames(raw);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Collects per-keyframe visual statistics (luma percentiles and saturation) for the credits
    /// fingerprint range.
    /// </summary>
    /// <remarks>
    /// Normally served from the row the keyframe scan in
    /// <see cref="DetectBlackFramesAsync(QueuedEpisode, int, CancellationToken)"/> wrote. Decodes on
    /// its own when that row is missing or unreadable, such as for an episode whose black-frame row
    /// predates that shared write. Empty when the ffmpeg check found the visuals filters missing.
    /// </remarks>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    /// <returns>A task that returns per-keyframe visual statistics relative to the credits fingerprint start.</returns>
    internal async Task<KeyframeVisual[]> DetectKeyframeVisualsAsync(QueuedEpisode episode, CancellationToken cancellationToken = default)
    {
        if (!_versionGate.CheckResult.KeyframeVisualsSupported)
        {
            return [];
        }

        // Normally a cache hit on the row the keyframe scan wrote. The decode below runs when that
        // row is missing or unreadable, such as for an episode whose black-frame row predates that
        // shared write. -to stops decoding near the window end; ParseKeyframeVisualsInWindow does
        // the bounding.
        var (start, end) = episode.GetFingerprintRange(AnalysisMode.Credits);
        var range = new TimeRange(start, end);
        return await RunCachedScanAsync(
            episode,
            AnalysisMode.Credits,
            CacheEntryType.KeyframeVisual,
            range.Start,
            range.End,
            async () =>
            {
                var codec = await GetVideoCodecAsync(episode, cancellationToken).ConfigureAwait(false);
                return
                [
                    .. KeyframeInput(codec, range.Start, episode.Path),
                    "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
                    .. OutputArgs(KeyframeFilters(codec, KeyframeVisualFilters)),
                ];
            },
            raw => ParseKeyframeVisualsInWindow(raw, range),
            cancellationToken).ConfigureAwait(false);
    }

    // One null output with its own filtergraph. Two of these on one input decode it once.
    private static string[] OutputArgs(string filters) => ["-an", "-dn", "-sn", "-vf", filters, "-f", "null", "-"];

    // The input of a keyframe-only decode of path from start, for a file whose default video
    // stream has the given codec (GetVideoCodecAsync).
    private static string[] KeyframeInput(string? codec, double start, string path) =>
    [
        "-skip_frame", "nokey",
        .. codec == "hevc" ? HevcKeyframeThreads : [],
        "-ss", start.ToString(CultureInfo.InvariantCulture),
        "-i", path,
    ];

    // A keyframe-only decode's filters, behind a keyframe select for VP9.
    private static string KeyframeFilters(string? codec, string filters) => codec == "vp9" ? $"{KeyframeSelect},{filters}" : filters;

    /// <inheritdoc/>
    public async Task<LumaWindow?> DecodeLumaWindowAsync(QueuedEpisode episode, TimeRange window, int width, CancellationToken cancellationToken = default)
    {
        // The keyframe scan's own format=yuv420p, then the luma plane: no range conversion either
        // way, so the frames read as the scan's signalstats did. showinfo logs each frame's time and
        // size at the info level. The rawvideo muxer syncs to a constant rate by default and would
        // duplicate or drop frames after showinfo counted them; passthrough writes exactly the
        // frames it logged.
        string[] args =
        [
            "-ss", FormatSeconds(window.Start),
            "-t", FormatSeconds(window.Duration),
            "-i", episode.Path,
            "-an", "-dn", "-sn",
            "-fps_mode", "passthrough",
            "-vf", $"scale={width.ToString(CultureInfo.InvariantCulture)}:-2,format=yuv420p,extractplanes=y,showinfo",
            "-f", "rawvideo", "-",
        ];

        // Sized for 16:9 frames at 30 fps, so the capture usually fills one buffer instead of
        // doubling its way there.
        var expectedBytes = (long)(width * (width * 9 / 16) * 30 * window.Duration);
        ProcessCapture capture;
        try
        {
            capture = await _processRunner.RunCapturedAsync(FFmpegPath, ProcessArgs(args, "info"), LumaWindowMaximumBytes, expectedBytes, ScanTimeout(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            LogLumaWindowFailed(ex, episode.Name);
            return null;
        }

        if (capture.Truncated || capture.ExitCode != 0)
        {
            LogLumaWindowUnusable(episode.Name, capture.ExitCode, capture.Truncated);
            return null;
        }

        var frames = FFmpegOutputParser.ParseShowInfo(capture.Stderr);
        if (frames.Length == 0
            || frames[0].Width <= 0
            || frames[0].Height <= 0
            || frames.Any(frame => frame.Width != frames[0].Width || frame.Height != frames[0].Height)
            || capture.Stdout.Length != (long)frames.Length * frames[0].Width * frames[0].Height)
        {
            LogLumaWindowMismatch(episode.Name, frames.Length, capture.Stdout.Length);
            return null;
        }

        return new LumaWindow(frames[0].Width, frames[0].Height, capture.Stdout, [.. frames.Select(frame => window.Start + frame.Time)]);
    }

    // Fixed-point seconds: ffmpeg's time parser rejects the exponent the default format gives a
    // value under 1e-5, such as a trim start a rounding error away from zero.
    private static string FormatSeconds(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Luma window of {Episode} could not be decoded")]
    private partial void LogLumaWindowFailed(Exception ex, string episode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Luma window of {Episode} unusable: ffmpeg exited with code {ExitCode}, output truncated at the byte cap: {Truncated}")]
    private partial void LogLumaWindowUnusable(string episode, int exitCode, bool truncated);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Luma window of {Episode} unusable: {FrameTimes} frame times for {Bytes} bytes of frames")]
    private partial void LogLumaWindowMismatch(string episode, int frameTimes, long bytes);

    // -to does not reliably bound a -skip_frame nokey scan (FFmpeg still emits keyframes past the
    // requested duration) and the keyframe scan runs to end of file, so every writer of a
    // KeyframeVisual row clips here. Times are relative to the -ss seek, so an in-window frame
    // falls within [0, window.Duration]; an unclipped run would let FindCreditRange select a
    // card run past CreditsFingerprintEnd and persist credits outside the scan window.
    private static KeyframeVisual[] ParseKeyframeVisualsInWindow(string raw, TimeRange window)
        => [.. FFmpegOutputParser.ParseKeyframeVisuals(raw).Where(v => v.Time >= 0 && v.Time <= window.Duration)];

    /// <inheritdoc/>
    public Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, TimeRange range, int threshold, int minimum, CancellationToken cancellationToken = default)
    {
        var pixelThreshold = FormatBlackDetectPixelThreshold(threshold);
        var pictureRatioThreshold = FormatBlackDetectPictureRatioThreshold(minimum);
        var minimumDuration = BlackInterval.MinimumDetectionDuration.ToString(CultureInfo.InvariantCulture);
        string[] args =
        [
            "-ss", range.Start.ToString(CultureInfo.InvariantCulture),
            "-skip_frame", "noref",
            "-i", episode.Path,
            "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
            "-an", "-dn", "-sn",
            "-vf", $"blackdetect=d={minimumDuration}:pix_th={pixelThreshold}:pic_th={pictureRatioThreshold}",
            "-f", "null", "-",
        ];

        var offset = range.Start - episode.CreditsFingerprintStart;
        return RunCachedScanAsync(
            episode,
            AnalysisMode.Credits,
            CacheEntryType.BlackInterval,
            range.Start,
            range.End,
            args,
            raw =>
            {
                var intervals = FFmpegOutputParser.ParseBlackIntervals(raw);
                return offset == 0
                    ? intervals
                    : [.. intervals.Select(interval => new BlackInterval(interval.Start + offset, interval.End + offset))];
            },
            cancellationToken);
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
        // -to runs after showinfo, so the parser clips keyframes logged past the requested window.
        var keyframes = await RunCachedScanAsync(
            episode,
            mode,
            CacheEntryType.Keyframe,
            range.Start,
            range.End,
            async () =>
            {
                var codec = await GetVideoCodecAsync(episode, cancellationToken).ConfigureAwait(false);
                return
                [
                    .. KeyframeInput(codec, range.Start, episode.Path),
                    "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
                    "-an", "-dn", "-sn",
                    "-vf", KeyframeFilters(codec, "showinfo"),
                    "-f", "null", "-",
                ];
            },
            raw => FFmpegOutputParser.ParseKeyFrames(raw, range.Start, _logger),
            cancellationToken).ConfigureAwait(false);
        return [.. keyframes.Where(time => time >= range.Start && time <= range.End)];
    }

    // A scan whose arguments need no probe of the file.
    private Task<T[]> RunCachedScanAsync<T>(
        QueuedEpisode episode,
        AnalysisMode mode,
        CacheEntryType entryType,
        double start,
        double end,
        IReadOnlyList<string> args,
        Func<string, T[]> parse,
        CancellationToken cancellationToken)
        => RunCachedScanAsync(episode, mode, entryType, start, end, () => Task.FromResult(args), parse, cancellationToken);

    /// <summary>
    /// Serves a detection scan from the cache or runs ffmpeg, parses its stderr and caches the result.
    /// </summary>
    /// <typeparam name="T">Element type of the scan result.</typeparam>
    /// <param name="episode">Episode being scanned.</param>
    /// <param name="mode">Analysis mode the cache row is keyed by.</param>
    /// <param name="entryType">Cache entry type.</param>
    /// <param name="start">Cache key start; must be the exact value used when the row was written.</param>
    /// <param name="end">Cache key end; must be the exact value used when the row was written.</param>
    /// <param name="args">Builds the ffmpeg arguments. Runs only on a cache miss, so it may probe the file first.</param>
    /// <param name="parse">Parses ffmpeg's stderr into the scan result. Runs only on a cache miss, so it may also record other results of the same run.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The cached or freshly parsed result.</returns>
    private async Task<T[]> RunCachedScanAsync<T>(
        QueuedEpisode episode,
        AnalysisMode mode,
        CacheEntryType entryType,
        double start,
        double end,
        Func<Task<IReadOnlyList<string>>> args,
        Func<string, T[]> parse,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_cacheService.TryRead(episode.EpisodeId, mode, entryType, start, end, out T[] cached))
        {
            return cached;
        }

        LogDetectionScan(_logger, entryType, start, end, episode.Path, episode.EpisodeId);

        var arguments = await args().ConfigureAwait(false);
        var raw = await RunScanAsync(arguments).ConfigureAwait(false);
        if (WithoutSliceThreads(arguments) is { } frameThreaded
            && SliceStructureErrors.Any(error => raw.Contains(error, StringComparison.Ordinal)))
        {
            LogSliceThreadsRetried(_logger, episode.Path, episode.EpisodeId);
            raw = await RunScanAsync(frameThreaded).ConfigureAwait(false);
        }

        var result = parse(raw);
        cancellationToken.ThrowIfCancellationRequested();
        _cacheService.Write(episode.EpisodeId, mode, entryType, start, end, result);

        return result;

        async Task<string> RunScanAsync(IReadOnlyList<string> scanArguments)
            => Encoding.UTF8.GetString(await GetOutputAsync(scanArguments, stderr: true, infoQuery: false, timeout: ScanTimeout(), cancellationToken).ConfigureAwait(false));
    }

    // The arguments with HevcKeyframeThreads taken out, or null when they don't hold it.
    private static string[]? WithoutSliceThreads(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i] == HevcKeyframeThreads[0] && arguments[i + 1] == HevcKeyframeThreads[1])
            {
                return [.. arguments.Take(i), .. arguments.Skip(i + 2)];
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var ffprobePath = GetFFprobePath();
            string[] args =
            [
                "-v", "error",
                "-select_streams", "a:0",
                "-show_entries", "stream=duration:stream_tags=DURATION",
                "-of", "csv=p=0",
                filePath,
            ];

            var output = Encoding.UTF8.GetString(await _processRunner.RunAsync(ffprobePath, args, stderr: false, timeout: 10 * 1000, cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();
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

    /// <inheritdoc />
    public async Task<SubtitleCue[]> ExtractSubtitleCuesAsync(QueuedEpisode episode, CancellationToken cancellationToken = default)
    {
        List<SubtitleCue> cues = [];
        List<int> embeddedStreams = [];
        Exception? probeFailure = null;
        Exception? sourceFailure = null;
        var probeOutputExceededLimit = false;
        var sourceReadSuccessfully = false;
        var subtitleBudgetExceeded = false;
        long subtitleBytesRead = 0;
        try
        {
            string[] probeArgs =
            [
                "-v", "error",
                "-select_streams", "s",
                "-show_entries", "stream=index,codec_name",
                "-of", "json",
                episode.Path,
            ];
            var probeCapture = await _processRunner.RunCapturedAsync(
                GetFFprobePath(),
                probeArgs,
                SubtitleProbeMaximumBytes,
                expectedStdoutBytes: 64 * 1024,
                timeout: 10 * 1000,
                cancellationToken).ConfigureAwait(false);
            if (probeCapture.Truncated)
            {
                probeOutputExceededLimit = true;
                throw new InvalidOperationException($"Subtitle probe output exceeded the {SubtitleProbeMaximumBytes} byte capture limit.");
            }

            if (probeCapture.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFprobe exited with code {probeCapture.ExitCode} while probing subtitles.");
            }

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(probeCapture.Stdout.Span));
            if (document.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var isImageSubtitle = stream.TryGetProperty("codec_name", out var codec)
                        && codec.ValueKind == JsonValueKind.String
                        && ImageSubtitleCodecs.Contains(codec.GetString()!);
                    if (!isImageSubtitle
                        && stream.TryGetProperty("index", out var index)
                        && index.TryGetInt32(out var streamIndex))
                    {
                        embeddedStreams.Add(streamIndex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!probeOutputExceededLimit
            && ex is (JsonException or IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException))
        {
            probeFailure = ex;
            LogSubtitleExtractionFailed(_logger, ex, episode.Path);
        }

        foreach (var streamIndex in embeddedStreams)
        {
            if (subtitleBudgetExceeded || subtitleBytesRead >= SubtitleEpisodeMaximumBytes)
            {
                subtitleBudgetExceeded = true;
                sourceFailure = new InvalidOperationException($"Subtitle sources exceeded the {SubtitleEpisodeMaximumBytes} byte episode capture limit.");
                break;
            }

            try
            {
                var extracted = await ExtractWebVttAsync(
                    episode.Path,
                    $"0:{streamIndex}",
                    SubtitleEpisodeMaximumBytes - subtitleBytesRead,
                    cancellationToken).ConfigureAwait(false);
                cues.AddRange(extracted.Cues);
                subtitleBytesRead += extracted.BytesRead;
                sourceReadSuccessfully = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                sourceFailure = ex;
                // ExtractWebVttAsync logged this source's failure; remaining streams may still be usable.
                if (ex is SubtitleOutputLimitException)
                {
                    subtitleBudgetExceeded = true;
                    break;
                }
            }
        }

        var sidecars = SubtitleSidecarFiles.FindTextSources(episode.Path);
        foreach (var sidecar in sidecars)
        {
            if (subtitleBudgetExceeded || subtitleBytesRead >= SubtitleEpisodeMaximumBytes)
            {
                subtitleBudgetExceeded = true;
                sourceFailure = new InvalidOperationException($"Subtitle sources exceeded the {SubtitleEpisodeMaximumBytes} byte episode capture limit.");
                break;
            }

            try
            {
                var extracted = await ExtractWebVttAsync(
                    sidecar,
                    "0:0",
                    SubtitleEpisodeMaximumBytes - subtitleBytesRead,
                    cancellationToken).ConfigureAwait(false);
                cues.AddRange(extracted.Cues);
                subtitleBytesRead += extracted.BytesRead;
                sourceReadSuccessfully = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                sourceFailure = ex;
                // ExtractWebVttAsync logged this source's failure; remaining sidecars may still be usable.
                if (ex is SubtitleOutputLimitException)
                {
                    subtitleBudgetExceeded = true;
                    break;
                }
            }
        }

        if (probeFailure is not null || sourceFailure is not null)
        {
            var failure = sourceFailure ?? probeFailure!;
            if (sourceReadSuccessfully)
            {
                throw new SubtitleExtractionException(
                    "One or more subtitle sources could not be read; the returned cues are incomplete.",
                    [.. cues],
                    failure);
            }

            throw new InvalidOperationException("Unable to read any embedded or sidecar subtitle source.", failure);
        }

        return [.. cues.Where(cue => cue.Start >= 0 && cue.End > cue.Start).OrderBy(cue => cue.Start)];
    }

    private async Task<(SubtitleCue[] Cues, long BytesRead)> ExtractWebVttAsync(
        string path,
        string map,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        string[] args =
        [
            "-i", path,
            "-map", map,
            "-c:s", "webvtt",
            "-f", "webvtt",
            "-",
        ];
        try
        {
            var capture = await _processRunner.RunCapturedAsync(
                FFmpegPath,
                ProcessArgs(args, "warning"),
                maximumBytes,
                expectedStdoutBytes: 64L * 1024,
                timeout: ScanTimeout(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (capture.Truncated)
            {
                throw new SubtitleOutputLimitException($"Subtitle output exceeded the {maximumBytes} byte capture limit.");
            }

            if (capture.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg exited with code {capture.ExitCode} while extracting subtitles.");
            }

            return (FFmpegOutputParser.ParseWebVtt(Encoding.UTF8.GetString(capture.Stdout.Span)), capture.Stdout.Length);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            LogSubtitleStreamExtractionFailed(_logger, ex, path, map);
            throw;
        }
    }

    /// <summary>
    /// Runs ffmpeg and returns standard output (or error).
    /// </summary>
    /// <param name="args">Arguments to pass to ffmpeg as individual tokens.</param>
    /// <param name="stderr"><see langword="true"/> to return standard error, where the detection
    /// filters print their results at info log level; otherwise, <see langword="false"/> for
    /// standard output at warning level.</param>
    /// <param name="infoQuery"><see langword="true"/> for a version or help query, which takes no
    /// input and rejects a trailing <c>-threads</c> option; otherwise, <see langword="false"/>.</param>
    /// <param name="timeout">Timeout (in milliseconds) to wait for ffmpeg to exit.</param>
    /// <param name="cancellationToken">Token used to cancel the FFmpeg process.</param>
    private Task<byte[]> GetOutputAsync(
        IReadOnlyList<string> args,
        bool stderr,
        bool infoQuery,
        int timeout,
        CancellationToken cancellationToken)
        => _processRunner.RunAsync(FFmpegPath, ProcessArgs(args, stderr ? "info" : "warning", infoQuery), stderr, timeout, cancellationToken);

    // Called only on a scan cache miss. Cache one probe per queued episode; new queue objects
    // re-probe replacement files.
    private Task<string?> GetVideoCodecAsync(QueuedEpisode episode, CancellationToken cancellationToken)
        => _videoCodecs.GetValue(episode, e => ProbeVideoCodecAsync(e.Path)).WaitAsync(cancellationToken);

    // The codec name of ffmpeg's default video stream rather than v:0, such as "hevc", without
    // decoding. Null when the probe fails or the file has no video stream.
    private async Task<string?> ProbeVideoCodecAsync(string filePath)
    {
        // The stream mapping block lists the stream as "Stream #0:1 -> #0:0 (hevc (native) -> ...".
        // Tag values print before the block and may hold the same text, so the search starts there.
        const string MappingBlock = "Stream mapping:";
        const string Mapping = " -> #0:0 (";
        try
        {
            string[] args = ["-i", filePath, "-an", "-dn", "-sn", "-frames:v", "0", "-f", "null", "-"];
            var output = Encoding.UTF8.GetString(await GetOutputAsync(
                args,
                stderr: true,
                infoQuery: false,
                timeout: 10 * 1000,
                CancellationToken.None).ConfigureAwait(false));

            var block = output.IndexOf(MappingBlock, StringComparison.Ordinal);
            var start = block < 0 ? -1 : output.IndexOf(Mapping, block, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += Mapping.Length;
            var end = output.IndexOf(' ', start);
            return end < 0 ? null : output[start..end];
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            LogVideoCodecProbeFailed(_logger, ex, filePath);
            return null;
        }
    }

    /// <summary>
    /// Prefixes a run's arguments with the ones every ffmpeg run gets: no banner, the configured
    /// thread count and the log level the caller reads its result at.
    /// </summary>
    /// <param name="args">The run's own arguments.</param>
    /// <param name="logLevel">The ffmpeg log level.</param>
    /// <param name="infoQuery"><see langword="true"/> for a version or help query, which takes no input and rejects <c>-threads</c>.</param>
    private static List<string> ProcessArgs(IReadOnlyList<string> args, string logLevel, bool infoQuery = false)
    {
        var processArgs = new List<string>(args.Count + 5) { "-hide_banner" };
        if (!infoQuery)
        {
            processArgs.Add("-threads");
            processArgs.Add((Plugin.Instance?.Configuration.ProcessThreads ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        processArgs.Add("-loglevel");
        processArgs.Add(logLevel);
        processArgs.AddRange(args);
        return processArgs;
    }

    /// <summary>
    /// Converts <see cref="Configuration.PluginConfiguration.ScanTimeoutSeconds"/> into the millisecond budget a
    /// fingerprint or detection scan gets. Zero or negative means no limit.
    /// </summary>
    /// <param name="seconds">The configured timeout in seconds.</param>
    /// <returns>Milliseconds, or <see cref="Timeout.Infinite"/> when unlimited.</returns>
    internal static int ScanTimeoutMilliseconds(int seconds) =>
        seconds > 0 ? (int)Math.Min(seconds * 1000L, int.MaxValue) : Timeout.Infinite;

    private static int ScanTimeout() => ScanTimeoutMilliseconds(Plugin.Instance?.Configuration.ScanTimeoutSeconds ?? 300);

    private static string GetFFprobePath()
    {
        var ffmpegPath = FFmpegPath;
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
            return new AudioStreamSelection(null, AudioStreamSelection.DefaultStreamCacheVariant);
        }

        // Probed on every fingerprint rather than memoized: the service is a singleton, and a file
        // replaced at the same path with a different stream layout must not keep a stale index.
        try
        {
            string[] args =
            [
                "-v", "error",
                "-select_streams", "a",
                "-show_entries", "stream=index,channels:stream_tags=language",
                "-of", "json",
                filePath,
            ];

            var output = Encoding.UTF8.GetString(await _processRunner.RunAsync(
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
                ? AudioStreamSelection.DefaultStreamCacheVariant
                : FormattableString.Invariant($"stream-index={selectedStream.Index}");

            return new AudioStreamSelection(
                preferMostChannels && selectsDefaultMostStream ? null : selectedStream.Index,
                cacheVariant);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            LogPreferredAudioLanguageProbeFailed(_logger, ex, filePath, preferredLanguage);
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheMode = QueuedEpisode.FingerprintCacheMode(mode);
        var (start, end) = episode.GetFingerprintRange(cacheMode);
        var configuration = Plugin.Instance?.Configuration;
        var preferredLanguage = ConfigHasher.NormalizeAudioLanguage(configuration?.PreferredAudioLanguage);
        var streamSelection = await FindAudioStreamSelectionAsync(
            episode.Path,
            preferredLanguage,
            configuration?.PreferAudioStreamWithMostChannels ?? true,
            cancellationToken).ConfigureAwait(false);
        var cacheVariant = streamSelection?.CacheVariant;

        // Rows written before stream selection existed came from FFmpeg's default stream, so
        // they are only reusable when that is still the effective stream.
        string? LegacyConfigHash(AnalysisMode rowMode) => streamSelection?.SelectsDefaultStream == true
            ? ConfigHasher.LegacyChromaprintCacheWithoutLanguage(configuration ?? new(), rowMode)
            : null;

        // Resolve the stream before reading the cache so a language preference can reuse a fingerprint
        // generated with the same effective stream under the default selection.
        if (_cacheService.TryRead(episode.EpisodeId, cacheMode, CacheEntryType.Chromaprint, start, end, out uint[] cachedFingerprint, cacheVariant, LegacyConfigHash(cacheMode)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cachedFingerprint;
        }

        // A row under the mode's own key was written before the row was shared. Copy it under
        // the shared key so the next read is one lookup; the old row stays until its item is deleted.
        if (cacheMode != mode
            && _cacheService.TryRead(episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end, out cachedFingerprint, cacheVariant, LegacyConfigHash(mode)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _cacheService.Write(episode.EpisodeId, cacheMode, CacheEntryType.Chromaprint, start, end, cachedFingerprint, cacheVariant);
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
            rawPoints = await GetOutputAsync(args, stderr: false, infoQuery: false, timeout: ScanTimeout(), cancellationToken).ConfigureAwait(false);
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

        var results = MemoryMarshal.Cast<byte, uint>(rawPoints).ToArray();

        // Try to cache this fingerprint.
        cancellationToken.ThrowIfCancellationRequested();
        _cacheService.Write(episode.EpisodeId, cacheMode, CacheEntryType.Chromaprint, start, end, results, cacheVariant);

        return results;
    }

    private static (int Index, int Channels, string? Language) SelectAudioStream(
        IReadOnlyList<(int Index, int Channels, string? Language)> streams,
        bool preferMostChannels)
        => preferMostChannels
            ? streams.OrderByDescending(stream => stream.Channels).ThenBy(stream => stream.Index).First()
            : streams.OrderBy(stream => stream.Index).First();

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while checking the installed FFmpeg version")]
    private static partial void LogFfmpegVersionCheckFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Output of ffmpeg {Arguments}: {Output}")]
    private static partial void LogFfmpegOutput(ILogger logger, string arguments, string output);

    [LoggerMessage(Level = LogLevel.Error, Message = "{ErrorMessage}")]
    private static partial void LogFfmpegRequirementFailed(ILogger logger, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fingerprinting [{Start}, {End}] from \"{File}\" (id {Id})")]
    private static partial void LogFingerprinting(ILogger logger, double start, double end, string file, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Type} scan [{Start}, {End}] of \"{File}\" (id {Id})")]
    private static partial void LogDetectionScan(ILogger logger, CacheEntryType type, double start, double end, string file, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "HEVC slice errors in \"{File}\" (id {Id}), decoding again with frame threads")]
    private static partial void LogSliceThreadsRetried(ILogger logger, string file, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The installed version of ffmpeg does not support the {Filter} filter; credits on a uniform card will not be detected")]
    private static partial void LogKeyframeVisualFilterUnsupported(ILogger logger, string filter);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not determine the video codec for \"{Path}\"; using standard frame handling")]
    private static partial void LogVideoCodecProbeFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chromaprint returned {Count} points for \"{Path}\"")]
    private static partial void LogChromaprintReturnedPoints(ILogger logger, int count, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to probe audio duration for {File}")]
    private static partial void LogAudioDurationProbeFailed(ILogger logger, Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to probe preferred audio language {Language} for {File}; using FFmpeg's default audio stream selection")]
    private static partial void LogPreferredAudioLanguageProbeFailed(ILogger logger, Exception ex, string file, string language);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to extract subtitles from {File}")]
    private static partial void LogSubtitleExtractionFailed(ILogger logger, Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to extract subtitle stream {Stream} from {File}")]
    private static partial void LogSubtitleStreamExtractionFailed(ILogger logger, Exception ex, string file, string stream);

    private sealed class SubtitleOutputLimitException : InvalidOperationException
    {
        public SubtitleOutputLimitException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// The audio stream a fingerprint is taken from.
    /// </summary>
    /// <param name="StreamIndex">Stream index to map explicitly, or <see langword="null"/> to leave FFmpeg's default selection unmapped.</param>
    /// <param name="CacheVariant">Effective stream identity that keys the fingerprint cache row.</param>
    private sealed record AudioStreamSelection(int? StreamIndex, string CacheVariant)
    {
        /// <summary>Cache variant of FFmpeg's default selection: most channels, then lowest index.</summary>
        public const string DefaultStreamCacheVariant = ConfigHasher.DefaultAudioStreamCacheVariant;

        /// <summary>Gets a value indicating whether the effective stream is FFmpeg's default one.</summary>
        public bool SelectsDefaultStream => CacheVariant == DefaultStreamCacheVariant;
    }
}
