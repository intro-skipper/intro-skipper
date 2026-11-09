// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using UtfUnknown;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides FFmpeg-based media analysis operations.
/// </summary>
internal sealed partial class FFmpegService : IFFmpegService
{
    private const double LimitedRangeLumaMinimum = 16.0;
    private const double LimitedRangeLumaRange = 219.0;

    // A cut decode's per-frame luma percentiles, mean saturation and luma difference to the frame
    // before. format=yuv420p brings 10-bit sources to 8-bit values and keeps the source's range, so
    // a full-range source spans 0 to 255. PageFacts measures the keyframe scan's pages on the same
    // scale from the same format.
    private const string KeyframeVisualFilters = "format=yuv420p,signalstats,metadata=print";

    // VP9/WebM may ignore -skip_frame nokey; filter expensive scans by packet keyframe.
    private const string KeyframeSelect = "select=eq(key\\,1)";

    // Bytes of each ffmpeg output stream a cut decode may hold at once. It writes no frames, and
    // metadata=print logs about 2 KB per frame, so the cap holds over 30000 frames and only stops a
    // runaway decode or a file whose diagnostics never end.
    private const long CutWindowMaximumBytes = 64L * 1024 * 1024;

    // Subtitle text should be small, but a malformed or malicious episode must not allow FFmpeg's
    // WebVTT files to grow without bound across its embedded streams and sidecars.
    private const long SubtitleEpisodeMaximumBytes = 16L * 1024 * 1024;

    // A media container can advertise an unbounded number of subtitle streams; cap ffprobe's
    // stream listing separately from the larger, decoded subtitle-text allowance.
    private const long SubtitleProbeMaximumBytes = 1024L * 1024;

    private const long ChapterProbeMaximumBytes = 4L * 1024 * 1024;

    // A zero-frame run prints the input dump and the stream mapping: a few KB, plus the file's
    // metadata tags. The cap only stops a file whose tags run to megabytes from being buffered.
    private const long VideoCodecProbeMaximumBytes = 1L * 1024 * 1024;

    // Warnings a subtitle extraction run may log before it is killed, so a stream that logs a
    // warning per packet cannot fill memory.
    private const long SubtitleLogMaximumBytes = 1024L * 1024;

    // Seconds the embedded subtitle run seeks before a cue window and reads past its end.
    private const double SubtitleSeekMargin = 10;
    private const double SubtitleStopMargin = 5;

    // Seconds a head list reads past the window. Its outpoint stops the file at the first packet
    // of any stream past it, so the margin leaves room for a cue a muxer stored after the audio
    // and video of the same time: ffmpeg's muxer holds a sparse stream back by up to 10 s.
    private const double SubtitleHeadListMargin = 15;

    // A keyframe-only decode with frame threads runs one keyframe at a time once keyframes sit
    // further apart than the frame-thread window, as they do in episodes. x265 codes HEVC with
    // wavefronts by default, and slice threads decode a keyframe's wavefront rows on several
    // cores. On a well-formed stream the thread type never changes the decoded pixels.
    private static readonly string[] HevcKeyframeThreads = ["-thread_type", "slice"];

    // HEVC decoder errors of the wavefront path, which slice threads use: it trusts each slice
    // header's entry points, and a stream whose slice segments don't match them decodes as black
    // with exit 0. A slice-threaded scan that logs one is decoded again with frame threads
    // (RunCachedScanAsync, ReadRawPagesAsync). "Independent slice segment missing." is not among
    // them: healthy streams with dependent slices log it on every skipped frame.
    internal static readonly string[] SliceStructureErrors = ["entry_point_offset table is corrupted", "WPP ctb addresses are wrong", "Previous slice segment missing"];

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

    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    private readonly ILogger<FFmpegService> _logger;
    private readonly DetectionCacheService _cacheService;
    private readonly FFmpegProcessRunner _processRunner;
    private readonly FFmpegVersionGate _versionGate;
    private readonly Func<EncodingOptions?> _encodingOptions;
    private readonly IMediaEncoder? _mediaEncoder;
    private readonly ISessionManager? _sessionManager;

    // Results of the probes that several modes repeat, kept for one pass.
    private readonly EpisodeProbeMemo<string, FFmpegOutputParser.VideoStream> _videoStreams = new();
    private readonly EpisodeProbeMemo<(string Path, string Language, bool PreferMostChannels), AudioStreamSelection> _audioStreamSelections = new();
    private readonly EpisodeProbeMemo<(string Path, string Languages), FFmpegOutputParser.SubtitleStreamListing> _subtitleStreamListings = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cacheService">The detection cache service.</param>
    /// <param name="mediaEncoder">Reports the hwaccels Jellyfin's ffmpeg supports.</param>
    /// <param name="sessionManager">Reports the sessions whose video transcodes keep the scans in software.</param>
    public FFmpegService(ILogger<FFmpegService> logger, DetectionCacheService cacheService, IMediaEncoder mediaEncoder, ISessionManager sessionManager)
        : this(logger, cacheService, null, null, null, mediaEncoder, sessionManager)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegService"/> class with a replaced version
    /// probe, encoding options or Jellyfin services, for tests and CreditsRunner, which run without
    /// Jellyfin.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cacheService">The detection cache service.</param>
    /// <param name="versionProbe">Replaces the ffmpeg version probe; <see langword="null"/> runs the real one.</param>
    /// <param name="versionProbeTimeout">Bounds one probe attempt; <see langword="null"/> uses the default.</param>
    /// <param name="encodingOptions">Reads the encoding options hardware decode follows, on every scan; <see langword="null"/> reads Jellyfin's.</param>
    /// <param name="mediaEncoder">Reports the hwaccels Jellyfin's ffmpeg supports; <see langword="null"/> treats every hwaccel as supported.</param>
    /// <param name="sessionManager">Reports the running sessions; <see langword="null"/> treats no session as transcoding.</param>
    internal FFmpegService(
        ILogger<FFmpegService> logger,
        DetectionCacheService cacheService,
        Func<CancellationToken, Task<bool>>? versionProbe = null,
        TimeSpan? versionProbeTimeout = null,
        Func<EncodingOptions?>? encodingOptions = null,
        IMediaEncoder? mediaEncoder = null,
        ISessionManager? sessionManager = null)
    {
        _logger = logger;
        _cacheService = cacheService;
        _encodingOptions = encodingOptions ?? (() => Plugin.Instance?.EncodingOptions);
        _mediaEncoder = mediaEncoder;
        _sessionManager = sessionManager;
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

            return (true, new FFmpegCheckResult("okay", [.. outputs]));
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
    public Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, PluginConfiguration config, CancellationToken cancellationToken = default)
    {
        // -vn, -sn, -dn: ignore video, subtitle, and data tracks
        var noise = config.SilenceDetectionMaximumNoise.ToString(CultureInfo.InvariantCulture);
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
        return RunCachedScanAsync(episode, mode, CacheEntryType.Silence, range.Start, range.End, config, args, hardwareDecodable: false, raw => FFmpegOutputParser.ParseSilence(raw, range.Start), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<BlackFrame[]> DetectBlackFramesAsync(
        QueuedEpisode episode,
        TimeRange range,
        int minimum,
        AnalysisMode mode,
        PluginConfiguration config,
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
            "-vf", $"blackframe=amount={amount}:threshold={config.BlackFrameThreshold}",
            "-f", "null", "-",
        ];

        // Only the recap scan decodes on hardware: the legacy analyzer's scans were not measured there.
        var allFrames = await RunCachedScanAsync(episode, mode, CacheEntryType.BlackFrame, range.Start, range.End, config, args, hardwareDecodable: mode == AnalysisMode.Recap, FFmpegOutputParser.ParseBlackFrames, cancellationToken).ConfigureAwait(false);
        return [.. allFrames.Where(bf => bf.Percentage >= minimum)];
    }

    /// <inheritdoc/>
    public async Task<KeyframePage[]> ScanKeyframesAsync(QueuedEpisode episode, PluginConfiguration config, CancellationToken cancellationToken = default)
    {
        var (start, end) = episode.GetFingerprintRange(AnalysisMode.Credits);
        if (!_cacheService.TryRead(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.KeyframePage, start, 0, config, out KeyframePage[] pages))
        {
            LogDetectionScan(_logger, CacheEntryType.KeyframePage, start, 0, episode.Path, episode.EpisodeId);
            pages = await ReadRawPagesAsync(episode, start, config, cancellationToken).ConfigureAwait(false);
            _cacheService.Write(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.KeyframePage, start, 0, config, pages);
        }

        // Clipped here rather than in the row, so where the window ends stays out of the key. A
        // visual past it would let the roll end and the card run reach past CreditsFingerprintEnd
        // and persist credits outside the scan window.
        var duration = end - start;
        return [.. pages.Select(page => page.Frame.Time <= duration ? page : page with { Visual = null })];
    }

    // One ffmpeg writing every keyframe from start to the end of the file down one pipe. Frames
    // keep their own size (-noautoscale) and all reach the pipe (passthrough), and format=yuv420p
    // brings 10-bit sources to 8-bit values and keeps the source's range. Hardware decode returns a 10-bit
    // 4:2:0 frame as p010, which format=yuv420p converts on another path than the yuv420p10le a
    // software decode gives, so a source that probes as yuv420p10le goes through yuv420p10le first
    // and keeps software's pixels. The slice retry runs in software.
    private async Task<KeyframePage[]> ReadRawPagesAsync(QueuedEpisode episode, double start, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var stream = await GetVideoStreamAsync(episode, cancellationToken).ConfigureAwait(false);
        var hardwareInput = await HardwareDecodeInputAsync(episode, config, cancellationToken).ConfigureAwait(false);
        var codec = stream?.Codec;
        var (pages, sliceStructureError) = await DecodeAsync(episode, hardwareInput, hardware => RunRawPagePipeAsync(Arguments(hardware))).ConfigureAwait(false);
        if (sliceStructureError && WithoutSliceThreads(Arguments([])) is { } frameThreaded)
        {
            LogSliceThreadsRetried(_logger, episode.Path, episode.EpisodeId);
            (pages, _) = (await RunRawPagePipeAsync(frameThreaded).ConfigureAwait(false)).Output;
        }

        return pages;

        string[] Arguments(string[] hardware) =>
        [
            .. hardware,
            .. KeyframeInput(codec, start, episode.Path),
            "-an", "-dn", "-sn",
            "-fps_mode", "passthrough", "-noautoscale",
            "-vf", KeyframeFilters(codec, hardware.Length > 0 && stream?.PixelFormat == "yuv420p10le" ? "format=yuv420p10le,format=yuv420p,showinfo=checksum=0" : "format=yuv420p,showinfo=checksum=0"),
            "-f", "rawvideo", "-",
        ];

        Task<((KeyframePage[] Pages, bool SliceStructureError) Output, int ExitCode)> RunRawPagePipeAsync(IReadOnlyList<string> arguments)
            => _processRunner.RunStreamingAsync(
                FFmpegPath,
                ProcessArgs(arguments, "info"),
                (stdout, stderr, kill) => RawPageReader.ReadAsync(stdout, stderr, kill, config.BlackFrameThreshold),
                ScanTimeout(),
                cancellationToken);
    }

    // The input of a keyframe-only decode of path from start, for a file whose mapped video
    // stream (the one ffmpeg decodes, GetVideoStreamAsync) has the given codec.
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
    public async Task<KeyframeVisual[]?> DecodeCutWindowAsync(QueuedEpisode episode, TimeRange window, CancellationToken cancellationToken = default)
    {
        // Every frame, at source resolution and in the keyframe scan's pixel format, so a frame's
        // difference is on the scale of the page's. Seeking to the earlier keyframe starts the
        // decode on it.
        string[] args =
        [
            "-ss", FormatSeconds(window.Start),
            "-t", FormatSeconds(window.Duration),
            "-i", episode.Path,
            "-an", "-dn", "-sn",
            "-vf", KeyframeVisualFilters,
            "-f", "null", "-",
        ];

        ProcessCapture capture;
        try
        {
            capture = await _processRunner.RunCapturedAsync(FFmpegPath, ProcessArgs(args, "info"), CutWindowMaximumBytes, expectedStdoutBytes: 0, ScanTimeout(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsProcessFailure(ex))
        {
            LogCutWindowFailed(ex, episode.Name);
            return null;
        }

        if (capture.Truncated || capture.ExitCode != 0)
        {
            LogCutWindowUnusable(episode.Name, capture.ExitCode, capture.Truncated);
            return null;
        }

        return [.. FFmpegOutputParser.ParseKeyframeVisuals(capture.Stderr).Select(frame => frame with { Time = window.Start + frame.Time })];
    }

    // Fixed-point seconds: ffmpeg's time parser rejects the exponent the default format gives a
    // value under 1e-5, such as a trim start a rounding error away from zero.
    private static string FormatSeconds(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cut window of {Episode} could not be decoded")]
    private partial void LogCutWindowFailed(Exception ex, string episode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cut window of {Episode} unusable: ffmpeg exited with code {ExitCode}, output truncated at the byte cap: {Truncated}")]
    private partial void LogCutWindowUnusable(string episode, int exitCode, bool truncated);

    /// <inheritdoc/>
    public Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, TimeRange range, int minimum, PluginConfiguration config, CancellationToken cancellationToken = default)
    {
        var pixelThreshold = FormatBlackDetectPixelThreshold(config.BlackFrameThreshold);
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

        // blackdetect prints times relative to the seek. The row holds absolute media times, the
        // time base of its key, so where the credits window starts never shifts them.
        return RunCachedScanAsync(
            episode,
            AnalysisMode.Credits,
            CacheEntryType.BlackInterval,
            range.Start,
            range.End,
            config,
            args,
            hardwareDecodable: true,
            raw => FFmpegOutputParser.ParseBlackIntervals(raw).Select(interval => new BlackInterval(interval.Start + range.Start, interval.End + range.Start)).ToArray(),
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
    public async Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, PluginConfiguration config, CancellationToken cancellationToken = default)
    {
        // -to runs after showinfo, so the parser clips keyframes logged past the requested window.
        var keyframes = await RunCachedScanAsync(
            episode,
            mode,
            CacheEntryType.Keyframe,
            range.Start,
            range.End,
            config,
            async () =>
            {
                var codec = (await GetVideoStreamAsync(episode, cancellationToken).ConfigureAwait(false))?.Codec;
                return
                [
                    .. KeyframeInput(codec, range.Start, episode.Path),
                    "-to", range.Duration.ToString(CultureInfo.InvariantCulture),
                    "-an", "-dn", "-sn",
                    "-vf", KeyframeFilters(codec, "showinfo"),
                    "-f", "null", "-",
                ];
            },
            hardwareDecodable: false,
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
        PluginConfiguration config,
        IReadOnlyList<string> args,
        bool hardwareDecodable,
        Func<string, T[]> parse,
        CancellationToken cancellationToken)
        => RunCachedScanAsync(episode, mode, entryType, start, end, config, () => Task.FromResult(args), hardwareDecodable, parse, cancellationToken);

    /// <summary>
    /// Serves a detection scan from the cache or runs ffmpeg, parses its stderr and caches the result.
    /// </summary>
    /// <typeparam name="T">Element type of the scan result.</typeparam>
    /// <param name="episode">Episode being scanned.</param>
    /// <param name="mode">Analysis mode the cache row is keyed by.</param>
    /// <param name="entryType">Cache entry type.</param>
    /// <param name="start">Cache key start; must be the exact value used when the row was written.</param>
    /// <param name="end">Cache key end; must be the exact value used when the row was written.</param>
    /// <param name="config">The configuration the arguments were built from; the row is read and written under its hash.</param>
    /// <param name="args">Builds the ffmpeg arguments. Runs only on a cache miss, so it may probe the file first.</param>
    /// <param name="hardwareDecodable">Whether the scan decodes on hardware under <see cref="PluginConfiguration.UseHardwareDecoding"/>, which then probes the file for its video stream when Jellyfin's gates let a method through.</param>
    /// <param name="parse">Parses ffmpeg's stderr into the scan result. Runs only on a cache miss, so it may also record other results of the same run.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The cached or freshly parsed result.</returns>
    private async Task<T[]> RunCachedScanAsync<T>(
        QueuedEpisode episode,
        AnalysisMode mode,
        CacheEntryType entryType,
        double start,
        double end,
        PluginConfiguration config,
        Func<Task<IReadOnlyList<string>>> args,
        bool hardwareDecodable,
        Func<string, T[]> parse,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_cacheService.TryRead(episode.EpisodeId, mode, entryType, start, end, config, out T[] cached))
        {
            return cached;
        }

        LogDetectionScan(_logger, entryType, start, end, episode.Path, episode.EpisodeId);

        var arguments = await args().ConfigureAwait(false);
        string[] hardware = hardwareDecodable ? await HardwareDecodeInputAsync(episode, config, cancellationToken).ConfigureAwait(false) : [];
        var raw = await DecodeAsync(episode, hardware, input => RunScanAsync([.. input, .. arguments])).ConfigureAwait(false);
        if (WithoutSliceThreads(arguments) is { } frameThreaded
            && SliceStructureErrors.Any(error => raw.Contains(error, StringComparison.Ordinal)))
        {
            LogSliceThreadsRetried(_logger, episode.Path, episode.EpisodeId);
            (raw, _) = await RunScanAsync(frameThreaded).ConfigureAwait(false);
        }

        var result = parse(raw);
        cancellationToken.ThrowIfCancellationRequested();
        _cacheService.Write(episode.EpisodeId, mode, entryType, start, end, config, result);

        return result;

        async Task<(string Output, int ExitCode)> RunScanAsync(IReadOnlyList<string> scanArguments)
        {
            var capture = await _processRunner.RunCapturedAsync(FFmpegPath, ProcessArgs(scanArguments, "info"), long.MaxValue, expectedStdoutBytes: 0, ScanTimeout(), cancellationToken).ConfigureAwait(false);
            return (capture.Stderr, capture.ExitCode);
        }
    }

    /// <summary>
    /// Runs a scan under hardware decode when <paramref name="hardware"/> holds input options, and
    /// in software when it holds none or the hardware run exits nonzero, times out or breaks the
    /// raw page protocol. Nothing the failed run wrote is kept. A software run's result stands
    /// whatever its exit code.
    /// </summary>
    /// <typeparam name="T">What the scan reads from ffmpeg.</typeparam>
    /// <param name="episode">The episode scanned, named in the log.</param>
    /// <param name="hardware">The input options from <see cref="HardwareDecodeInputAsync"/>.</param>
    /// <param name="run">Runs ffmpeg with the given input options in front of the scan's own arguments.</param>
    /// <returns>The accepted run's output.</returns>
    private async Task<T> DecodeAsync<T>(QueuedEpisode episode, string[] hardware, Func<string[], Task<(T Output, int ExitCode)>> run)
    {
        if (hardware.Length > 0)
        {
            try
            {
                var (output, exitCode) = await run(hardware).ConfigureAwait(false);
                if (exitCode == 0)
                {
                    return output;
                }

                LogHardwareDecodeFailed(_logger, episode.Path, episode.EpisodeId, exitCode);
            }
            catch (Exception ex) when (ex is InvalidDataException || IsProcessFailure(ex))
            {
                // A hung device, a broken pipe or a process that died partway through a frame is the hardware
                // run's failure, so software gets a full timeout of its own.
                LogHardwareDecodeBroke(_logger, episode.Path, episode.EpisodeId, ex.Message);
            }
        }

        return (await run([]).ConfigureAwait(false)).Output;
    }

    // The hardware decode input options of a scan under config and the server's current encoding
    // options, or none. Probes the file only when Jellyfin's gates let a method through.
    private async Task<string[]> HardwareDecodeInputAsync(QueuedEpisode episode, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!config.UseHardwareDecoding
            || _encodingOptions() is not { } encoding
            || HardwareDecodeMethod(encoding) is not { Length: > 0 } method
            || !JellyfinAllowsHardwareDecode(method))
        {
            return [];
        }

        FFmpegOutputParser.VideoStream? stream;
        try
        {
            stream = await GetVideoStreamAsync(episode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException || IsProcessFailure(ex))
        {
            // The recap and interval scans ask for the stream only to choose hardware. A failed
            // probe puts them back on the software path they ran before hardware decode, which
            // reports a real problem with the file itself. The keyframe scan probed first, so a
            // probe failure there has already thrown.
            LogHardwareDecodeProbeFailed(ex, episode.Path);
            return [];
        }

        return DecodesOnHardware(encoding, stream) ? [.. method, "-xerror"] : [];
    }

    // Jellyfin's two gates, read when a scan starts. A transcode starting later shares the
    // decoder with that scan until the scan ends: Jellyfin has no decoder slot count to wait on.
    // An audio-only transcode also reports IsVideoDirect false, but with no VideoCodec. NowPlayingItem
    // is left out because a transcode can run before its client reports playing. The cost is that
    // a TranscodingInfo Jellyfin leaves set after a stop report with no PlaySessionId keeps scans in
    // software until the device's next report.
    private bool JellyfinAllowsHardwareDecode(string[] method)
        => (_mediaEncoder?.SupportsHwaccel(method[Array.IndexOf(method, "-hwaccel") + 1]) ?? true)
            && !(_sessionManager?.Sessions.Any(session => session.TranscodingInfo is { IsVideoDirect: false, VideoCodec: not null }) ?? false);

    /// <summary>
    /// The hwaccel input options Jellyfin's playback decodes with under its acceleration type, or
    /// none when the scans decode in software.
    /// </summary>
    /// <remarks>
    /// VAAPI decodes on Jellyfin's VAAPI device and only on Linux, NVENC through CUDA, AMF through
    /// D3D11VA on the first AMD adapter and only on Windows, and QSV through VAAPI on Linux
    /// (<see cref="LinuxQsvMethod"/>), as Jellyfin does. QSV on Windows, VideoToolbox, RKMPP and V4L2
    /// were not measured and decode in software. No output format is set, so each frame comes back
    /// to system memory.
    /// </remarks>
    /// <param name="encoding">Jellyfin's encoding options.</param>
    /// <returns>The options to place before <c>-i</c>, or none.</returns>
    internal static string[] HardwareDecodeMethod(EncodingOptions encoding) => encoding.HardwareAccelerationType switch
    {
        HardwareAccelerationType.vaapi when OperatingSystem.IsLinux() && !string.IsNullOrEmpty(encoding.VaapiDevice) => ["-hwaccel", "vaapi", "-hwaccel_device", encoding.VaapiDevice],
        HardwareAccelerationType.vaapi when OperatingSystem.IsLinux() => ["-hwaccel", "vaapi"],
        HardwareAccelerationType.nvenc => ["-hwaccel", "cuda"],
        HardwareAccelerationType.amf when OperatingSystem.IsWindows() => ["-init_hw_device", "d3d11va=dx11:,vendor=0x1002", "-hwaccel", "d3d11va"],
        HardwareAccelerationType.qsv when OperatingSystem.IsLinux() => LinuxQsvMethod(encoding),
        _ => [],
    };

    /// <summary>
    /// The hwaccel options of QSV on Linux, which decode the way Jellyfin's default native path
    /// does: through VAAPI on the device Jellyfin derives its QSV device from, or none when
    /// <see cref="EncodingOptions.PreferSystemNativeHwDecoder"/> is off.
    /// </summary>
    /// <remarks>
    /// Jellyfin names the device by render node when <see cref="EncodingOptions.QsvDevice"/> is an
    /// existing file and by vendor otherwise, and takes the vendor form only from ffmpeg 7.0.1 on.
    /// The plugin requires ffmpeg 7.1.3, and on an older one device setup fails and the scan reruns
    /// in software.
    /// </remarks>
    /// <param name="encoding">Jellyfin's encoding options.</param>
    /// <returns>The options to place before <c>-i</c>, or none.</returns>
    internal static string[] LinuxQsvMethod(EncodingOptions encoding) => encoding.PreferSystemNativeHwDecoder
        ? ["-init_hw_device", (File.Exists(encoding.QsvDevice) ? $"vaapi=va:{encoding.QsvDevice}" : "vaapi=va:,vendor_id=0x8086") + ",driver=iHD", "-hwaccel", "vaapi"]
        : [];

    /// <summary>
    /// Whether a scan decodes the stream on hardware, given a method: only the formats measured to
    /// give software's frames, which Jellyfin's playback also decodes on hardware.
    /// </summary>
    /// <remarks>
    /// H.264 and HEVC at <c>yuv420p</c> or <c>yuvj420p</c> qualify, and 10-bit 4:2:0 HEVC while
    /// Jellyfin decodes 10-bit HEVC on hardware, each only while Jellyfin decodes the codec on
    /// hardware. HEVC RExt (4:2:2, 4:4:4, 12-bit), Hi10P H.264 and AV1 stay in software: VAAPI
    /// applies AV1 film grain differently from software, and the others were not measured.
    /// </remarks>
    /// <param name="encoding">Jellyfin's encoding options.</param>
    /// <param name="stream">The video stream the scan decodes, or <see langword="null"/> when there is none.</param>
    /// <returns><see langword="true"/> when the stream decodes on hardware.</returns>
    internal static bool DecodesOnHardware(EncodingOptions encoding, FFmpegOutputParser.VideoStream? stream)
        => stream is { Codec: "h264" or "hevc" }
            && encoding.HardwareDecodingCodecs.Contains(stream.Codec, StringComparer.OrdinalIgnoreCase)
            && stream.PixelFormat switch
            {
                "yuv420p" or "yuvj420p" => true,
                "yuv420p10le" => stream.Codec == "hevc" && encoding.EnableDecodingColorDepth10Hevc,
                _ => false,
            };

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

                // A Matroska DURATION tag is HH:MM:SS with nine fractional digits, two more than TimeSpan parses.
                if (value.Split(':') is [var hours, var minutes, var tagSeconds]
                    && int.TryParse(hours, CultureInfo.InvariantCulture, out var h)
                    && int.TryParse(minutes, CultureInfo.InvariantCulture, out var m)
                    && double.TryParse(tagSeconds, CultureInfo.InvariantCulture, out var s)
                    && (h * 3600) + (m * 60) + s is var total and > 0)
                {
                    return total;
                }
            }
        }
        catch (Exception ex) when (IsProcessFailure(ex))
        {
            LogAudioDurationProbeFailed(_logger, ex, filePath);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<ChapterInfo[]?> GetEmbeddedChaptersAsync(QueuedEpisode episode, CancellationToken cancellationToken = default)
    {
        try
        {
            string[] args =
            [
                "-v", "error",
                "-show_chapters",
                "-of", "json",
                episode.Path,
            ];

            var capture = await _processRunner.RunCapturedAsync(
                GetFFprobePath(),
                args,
                ChapterProbeMaximumBytes,
                expectedStdoutBytes: 4 * 1024,
                timeout: 10 * 1000,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (capture.ExitCode != 0 || capture.Truncated)
            {
                LogEmbeddedChapterProbeUnusable(episode.Path, capture.ExitCode, capture.Truncated);
                return null;
            }

            return FFmpegOutputParser.ParseChapters(
                Encoding.UTF8.GetString(capture.Stdout.Span),
                TickConversions.FromSeconds(episode.Duration),
                Plugin.Instance?.ChapterNameFormat ?? "Chapter {0}");
        }
        catch (Exception ex) when (ex is JsonException || IsProcessFailure(ex))
        {
            LogEmbeddedChapterProbeFailed(ex, episode.Path);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<SubtitleScan> ExtractSubtitleCuesAsync(QueuedEpisode episode, TimeRange window, CancellationToken cancellationToken = default)
    {
        List<SubtitleCue> cues = [];
        var complete = false;
        try
        {
            var languages = SubtitleLanguageSelection.Parse(Plugin.Instance?.Configuration.SubtitleLanguages);
            var listing = await _subtitleStreamListings.GetAsync(
                episode,
                (episode.Path, languages.Normalized),
                ct => ProbeTextSubtitleStreamsAsync(episode.Path, languages, ct),
                cancellationToken).ConfigureAwait(false);
            var sidecars = SubtitleSidecarFiles.FindTextSources(episode, languages);
            complete = listing is not null;
            var outputCount = (listing?.Streams.Length ?? 0) + sidecars.Length;
            if (outputCount == 0)
            {
                return new SubtitleScan([], complete);
            }

            // Each output gets an equal share of the episode's cap, so the files on disk stay within it.
            var outputLimit = SubtitleEpisodeMaximumBytes / outputCount;
            var directory = Directory.CreateDirectory(Path.Join(
                Plugin.Instance?.TempDirectory ?? Path.GetTempPath(),
                "introskipper-subtitles-" + Guid.NewGuid().ToString("N")));
            try
            {
                List<SubtitleInput> inputs = [];
                if (listing is { Streams: { Length: > 0 } embeddedStreams })
                {
                    // The margins keep every cue that starts inside the window clear of the
                    // seek and stop points.
                    var seek = window.Start - SubtitleSeekMargin;
                    var stop = window.End + SubtitleStopMargin;
                    var outputPrefix = Path.Join(directory.FullName, "embedded");
                    if (seek <= 0
                        && stop < episode.Duration
                        && listing.MatroskaStartTime is { } startTime
                        && !episode.Path.AsSpan().ContainsAny('\r', '\n'))
                    {
                        var outpoint = startTime + window.End + SubtitleHeadListMargin;
                        var list = await WriteHeadListAsync(episode.Path, outpoint, outputPrefix, cancellationToken).ConfigureAwait(false);
                        inputs.Add(new(list, null, null, embeddedStreams, outputPrefix, Concat: true));
                    }
                    else
                    {
                        inputs.Add(new(episode.Path, seek > 0 ? seek : null, stop < episode.Duration ? stop : null, embeddedStreams, outputPrefix));
                    }
                }

                for (var i = 0; i < sidecars.Length; i++)
                {
                    // A sidecar that cannot be read fails only itself: the scan goes on without
                    // it and ends incomplete.
                    try
                    {
                        var outputPrefix = Path.Join(directory.FullName, i.ToString(CultureInfo.InvariantCulture));
                        var source = await ReadableSidecarAsync(sidecars[i], outputPrefix, cancellationToken).ConfigureAwait(false);
                        inputs.Add(new(source, null, null, [0], outputPrefix));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogSubtitleScanFailed(ex, sidecars[i]);
                        complete = false;
                    }
                }

                if (inputs.Count > 0)
                {
                    complete &= await ExtractWebVttAsync(inputs, outputLimit, cues, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                DeleteSubtitleOutputs(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Listing the sidecars, creating the output directory or reading an output can fail
            // on one episode's files. The scan then ends with the cues read so far and is
            // incomplete, so the episode is retried; thrown, it would end the season's pass.
            LogSubtitleScanFailed(ex, episode.Path);
            complete = false;
        }

        return new SubtitleScan([.. cues.OrderBy(cue => cue.Start)], complete);
    }

    // The file ffmpeg should read for a sidecar. ffmpeg reads text subtitles as UTF-8 and drops
    // every cue that is not, while the run still succeeds. A sidecar with a UTF-16 byte order
    // mark, which ffmpeg converts itself, or with valid UTF-8 is read as is. Any other is decoded
    // with the charset UtfUnknown detects (the detector Jellyfin ships), ISO-8859-1 widened to
    // its superset Windows-1252, or Windows-1252 when nothing is detected, and written as a UTF-8
    // copy at copyPrefix with the sidecar's extension. ffmpeg's -sub_charenc is not used: iconv
    // rejects some charset names the detector reports, and a wrong guess would garble UTF-8. A
    // sidecar larger than the episode's subtitle cap is read as is rather than loaded into memory.
    private async Task<string> ReadableSidecarAsync(string path, string copyPrefix, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > SubtitleEpisodeMaximumBytes)
        {
            return path;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] || Utf8.IsValid(bytes))
        {
            return path;
        }

        var encoding = CharsetDetector.DetectFromBytes(bytes).Detected is { Encoding: { } detected, EncodingName: not "iso-8859-1" } ? detected : Windows1252;
        var copy = copyPrefix + Path.GetExtension(path);
        await File.WriteAllTextAsync(copy, encoding.GetString(bytes), cancellationToken).ConfigureAwait(false);
        LogSidecarDecoded(path, encoding.WebName);
        return copy;
    }

    // The embedded subtitle streams to read, by stream index: those ffmpeg decodes to text, in a
    // selected language, and the file's start time when it is Matroska. Null when ffprobe failed
    // (logged), which leaves the scan incomplete.
    private async Task<FFmpegOutputParser.SubtitleStreamListing?> ProbeTextSubtitleStreamsAsync(string path, SubtitleLanguageSelection languages, CancellationToken cancellationToken)
    {
        string[] args =
        [
            "-v", "error",
            "-select_streams", "s",
            "-show_entries", "stream=index,codec_name:stream_tags=language:format=format_name,start_time",
            "-of", "compact=p=1:nk=1",
            path,
        ];
        ProcessCapture capture;
        try
        {
            capture = await _processRunner.RunCapturedAsync(GetFFprobePath(), args, SubtitleProbeMaximumBytes, expectedStdoutBytes: 4 * 1024, timeout: 10 * 1000, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsProcessFailure(ex))
        {
            LogSubtitleProbeFailed(ex, path);
            return null;
        }

        if (capture.Truncated || capture.ExitCode != 0)
        {
            LogSubtitleProbeUnusable(path, capture.ExitCode, capture.Truncated);
            return null;
        }

        return FFmpegOutputParser.ParseSubtitleStreamListing(Encoding.UTF8.GetString(capture.Stdout.Span), languages);
    }

    // Writes an ffconcat list at listPrefix + ".ffconcat" that reads the Matroska file at path up
    // to outpoint, a second on the file's own clock, and returns the list's path. The concat
    // demuxer stops every stream of the file there, while an output -t stops only once its
    // stream delivers a cue past the stop, so a sparse track would hold the read open to the end
    // of the file. The list quotes the path in single quotes, so a quote in it is written as
    // '\''; a line break cannot be listed, so the caller keeps such a path out. Matroska only: the
    // concat demuxer does not pass unread streams down to the file it wraps, so on an MP4, whose
    // demuxer otherwise reads only the subtitle samples, it read more.
    private static async Task<string> WriteHeadListAsync(string path, double outpoint, string listPrefix, CancellationToken cancellationToken)
    {
        var list = listPrefix + ".ffconcat";
        var quoted = path.Replace("'", @"'\''", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            list,
            FormattableString.Invariant($"ffconcat version 1.0\nfile '{quoted}'\noutpoint {FormatSeconds(outpoint)}\n"),
            cancellationToken).ConfigureAwait(false);
        return list;
    }

    // Decodes the streams of every input to WebVTT in a single ffmpeg run, one file per stream
    // (SubtitleInput.OutputPath), each capped at outputLimit bytes by ffmpeg's -fs. A run demuxes
    // each input from its seek point until every output of it has stopped, so the embedded
    // streams and the sidecars share one run: a run per stream would read a file with 40 text
    // tracks 40 times, and a run per sidecar costs a process start each. Adds to cues, unsorted,
    // every output under its cap, including those of a run that failed or was killed, which
    // stop where the run did. Returns false when a run failed (logged), or an output is missing
    // or reached its cap.
    //
    // ffmpeg opens every input before it creates an output, and exits when it cannot open one,
    // such as an empty or garbled sidecar. A failed run that left no output therefore stopped
    // while opening its inputs, and each input then runs alone, so such an input fails only
    // itself.
    //
    // A seek skips its input before that media second. -copyts keeps the cue times on the
    // file's clock and -start_at_zero starts that clock at zero, as a read from the start does,
    // so the times match a full read; without either flag they shift by the seek point or by
    // the file's start time. Both flags apply to every input of the run and leave a sidecar's
    // times as they are. -sseof is avoided: when the tail it asks for is longer than the file,
    // every cue shifts. A stop ends each output of its input at that media second with an
    // output -t, which counts on the clock of the cue times, with or without a seek. An input -t
    // would bound only copied streams, not decoded ones.
    //
    // A concat input is an ffconcat list (WriteHeadListAsync), read with -safe 0 because it
    // names an absolute path. The concat demuxer already counts from the file's start time, so
    // a run with one takes -copyts alone: without it, ffmpeg 8.1 subtracts a start time the
    // concat demuxer derives from the packets, which counts an audio codec delay (AAC 23 ms,
    // Opus 7 ms) that a plain read leaves out, and every cue shifts by it. A run never holds a
    // seek and a concat input together. -auto_convert 0 keeps the concat demuxer from putting
    // every H.264 video packet through h264_mp4toannexb: the run decodes no video, and the
    // filter ends the input at a damaged packet while ffmpeg still exits 0.
    private async Task<bool> ExtractWebVttAsync(
        IReadOnlyList<SubtitleInput> inputs,
        long outputLimit,
        List<SubtitleCue> cues,
        CancellationToken cancellationToken)
    {
        List<string> args = inputs.Any(input => input.Seek is not null) ? ["-copyts", "-start_at_zero"]
            : inputs.Any(input => input.Concat) ? ["-copyts"]
            : [];
        foreach (var input in inputs)
        {
            if (input.Seek is { } seek)
            {
                args.AddRange(["-ss", FormatSeconds(seek)]);
            }

            if (input.Concat)
            {
                args.AddRange(["-f", "concat", "-safe", "0", "-auto_convert", "0"]);
            }

            args.AddRange(["-i", input.Path]);
        }

        List<(SubtitleInput Input, int Stream)> outputs = [];
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            foreach (var stream in input.Streams)
            {
                outputs.Add((input, stream));
                args.AddRange(
                [
                    "-map", FormattableString.Invariant($"{i}:{stream}"),
                    .. input.Stop is { } stop ? ["-t", FormatSeconds(stop)] : Array.Empty<string>(),
                    "-c:s", "webvtt",
                    "-fs", outputLimit.ToString(CultureInfo.InvariantCulture),
                    "-f", "webvtt",
                    input.OutputPath(stream),
                ]);
            }
        }

        var files = string.Join(", ", inputs.Select(input => input.Path));
        var ran = true;
        try
        {
            var capture = await _processRunner.RunCapturedAsync(FFmpegPath, ProcessArgs(args, "warning"), SubtitleLogMaximumBytes, expectedStdoutBytes: 0, ScanTimeout(), cancellationToken).ConfigureAwait(false);
            if (capture.Truncated || capture.ExitCode != 0)
            {
                LogSubtitleRunUnusable(files, capture.ExitCode, capture.Truncated);
                ran = false;
            }
        }
        catch (Exception ex) when (IsProcessFailure(ex))
        {
            LogSubtitleRunFailed(ex, files);
            ran = false;
        }

        if (!ran && inputs.Count > 1 && !outputs.Exists(output => File.Exists(output.Input.OutputPath(output.Stream))))
        {
            var everyInputComplete = true;
            foreach (var input in inputs)
            {
                everyInputComplete &= await ExtractWebVttAsync([input], outputLimit, cues, cancellationToken).ConfigureAwait(false);
            }

            return everyInputComplete;
        }

        var complete = ran;
        foreach (var (input, stream) in outputs)
        {
            var output = new FileInfo(input.OutputPath(stream));
            if (!output.Exists)
            {
                complete = false;
            }
            else if (output.Length >= outputLimit)
            {
                LogSubtitleStreamTruncated(input.Path, stream, outputLimit);
                complete = false;
            }
            else
            {
                cues.AddRange(FFmpegOutputParser.ParseWebVtt(await File.ReadAllTextAsync(output.FullName, cancellationToken).ConfigureAwait(false)));
            }
        }

        return complete;
    }

    // A leftover directory must not fail the scan that wrote it: Jellyfin's cache cleanup task
    // clears stale files from its temp directory.
    private void DeleteSubtitleOutputs(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSubtitleOutputsNotDeleted(ex, directory.FullName);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not list the subtitle streams of {File}")]
    private partial void LogSubtitleProbeFailed(Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Subtitle stream list of {File} unusable: ffprobe exited with code {ExitCode}, output truncated at the byte cap: {Truncated}")]
    private partial void LogSubtitleProbeUnusable(string file, int exitCode, bool truncated);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not extract the subtitle streams of {Files}")]
    private partial void LogSubtitleRunFailed(Exception ex, string files);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Embedded chapter probe of {File} unusable: ffprobe exited with code {ExitCode}, output truncated at the byte cap: {Truncated}")]
    private partial void LogEmbeddedChapterProbeUnusable(string file, int exitCode, bool truncated);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read embedded chapters from {File}")]
    private partial void LogEmbeddedChapterProbeFailed(Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Subtitle extraction from {Files} incomplete: ffmpeg exited with code {ExitCode}, log truncated at the byte cap: {Truncated}")]
    private partial void LogSubtitleRunUnusable(string files, int exitCode, bool truncated);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Subtitle stream {Stream} of {File} reached the {Limit} byte cap")]
    private partial void LogSubtitleStreamTruncated(string file, int stream, long limit);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Subtitle sidecar {File} is not UTF-8; reading it as {Charset}")]
    private partial void LogSidecarDecoded(string file, string charset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the subtitle extraction directory {Directory}")]
    private partial void LogSubtitleOutputsNotDeleted(Exception ex, string directory);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Subtitle scan of {File} incomplete: a file operation failed")]
    private partial void LogSubtitleScanFailed(Exception ex, string file);

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

    // Called only on a scan cache miss, so an episode whose scans are all cached is never probed.
    private Task<FFmpegOutputParser.VideoStream?> GetVideoStreamAsync(QueuedEpisode episode, CancellationToken cancellationToken)
        => _videoStreams.GetAsync(episode, episode.Path, ct => ProbeVideoStreamAsync(episode.Path, ct), cancellationToken);

    // The video stream a scan decodes, its codec such as "hevc" and pixel format, read from the stream
    // mapping of an ffmpeg run that decodes no frame. ffmpeg ranks the video streams by frame
    // size, the default disposition and whether the stream delivered packets while ffmpeg
    // probed the file. ffprobe cannot report the last, so no ffprobe rule can copy the choice.
    // Null when the file has no video stream. A run that fails to start, times out or is cut at
    // the cap before its stream mapping throws instead, since the scan would cache a VP9 file's
    // every frame as a keyframe.
    private async Task<FFmpegOutputParser.VideoStream?> ProbeVideoStreamAsync(string filePath, CancellationToken cancellationToken)
    {
        string[] args = ["-i", filePath, "-an", "-dn", "-sn", "-frames:v", "0", "-f", "null", "-"];

        // The mapping block follows the input dump, which prints every metadata tag, so the
        // capture is capped rather than buffered whole.
        var capture = await _processRunner.RunCapturedAsync(
            FFmpegPath,
            ProcessArgs(args, "info"),
            VideoCodecProbeMaximumBytes,
            expectedStdoutBytes: 0,
            ScanTimeout(),
            cancellationToken).ConfigureAwait(false);
        var stream = FFmpegOutputParser.ParseMappedVideoStream(capture.Stderr);
        if (stream is null && capture.Truncated)
        {
            throw new InvalidDataException($"The ffmpeg log of \"{filePath}\" passed {VideoCodecProbeMaximumBytes} bytes before its stream mapping");
        }

        return stream;
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

    // What a failed ffmpeg or ffprobe run throws through the process runner: the process could
    // not start (Win32Exception, InvalidOperationException), its pipes or files failed, or it
    // was killed at its timeout. Cancellation is not among them and always propagates.
    private static bool IsProcessFailure(Exception ex)
        => ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException;

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

    // The audio stream to fingerprint. Null when the probe failed or listed no audio stream: that
    // fingerprint reads ffmpeg's default stream, and the next one of the episode probes again.
    private Task<AudioStreamSelection?> GetAudioStreamSelectionAsync(
        QueuedEpisode episode,
        string preferredLanguage,
        bool preferMostChannels,
        CancellationToken cancellationToken)
    {
        // ffmpeg's own pick (the most-channels policy, see SelectAudioStream) needs no probe or map.
        if (string.IsNullOrWhiteSpace(preferredLanguage) && preferMostChannels)
        {
            return Task.FromResult<AudioStreamSelection?>(new AudioStreamSelection(null, AudioStreamSelection.DefaultStreamCacheVariant, SelectsDefaultStream: true));
        }

        return _audioStreamSelections.GetAsync(
            episode,
            (episode.Path, preferredLanguage, preferMostChannels),
            ct => FindAudioStreamSelectionAsync(episode.Path, preferredLanguage, preferMostChannels, ct),
            cancellationToken);
    }

    private async Task<AudioStreamSelection?> FindAudioStreamSelectionAsync(
        string filePath,
        string preferredLanguage,
        bool preferMostChannels,
        CancellationToken cancellationToken)
    {
        var hasLanguagePreference = !string.IsNullOrWhiteSpace(preferredLanguage);
        try
        {
            string[] args =
            [
                "-v", "error",
                "-select_streams", "a",
                "-show_entries", "stream=index,channels:stream_tags=language:stream_disposition=default",
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

            var audioStreams = new List<(int Index, int Channels, string? Language, bool Default)>();
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
                    var isDefault = stream.TryGetProperty("disposition", out var disposition) &&
                        disposition.TryGetProperty("default", out var defaultElement) &&
                        defaultElement.TryGetInt32(out var defaultFlag) &&
                        defaultFlag == 1;

                    audioStreams.Add((streamIndex, channels, language, isDefault));
                }
            }

            if (audioStreams.Count == 0)
            {
                return null;
            }

            var defaultStream = SelectAudioStream(audioStreams, preferMostChannels: true);

            // 12.0 wrote rows under the default variant for its own pick, most channels then lowest index,
            // mapping that stream explicitly. Where that is not ffmpeg's pick, those rows hold either stream.
            var defaultVariantIsUnambiguous = defaultStream.Index == audioStreams.OrderByDescending(stream => stream.Channels).ThenBy(stream => stream.Index).First().Index;
            var candidates = hasLanguagePreference
                ? audioStreams.Where(stream => string.Equals(stream.Language, preferredLanguage, StringComparison.OrdinalIgnoreCase)).ToList()
                : audioStreams;

            if (candidates.Count == 0)
            {
                // An unmatched language preference falls back to all audio streams using the configured policy.
                candidates = audioStreams;
            }

            var selectedStream = SelectAudioStream(candidates, preferMostChannels);
            var selectsDefaultStream = selectedStream.Index == defaultStream.Index;
            var cacheVariant = selectsDefaultStream && defaultVariantIsUnambiguous
                ? AudioStreamSelection.DefaultStreamCacheVariant
                : FormattableString.Invariant($"stream-index={selectedStream.Index}");

            return new AudioStreamSelection(selectedStream.Index, cacheVariant, selectsDefaultStream);
        }
        catch (Exception ex) when (ex is JsonException || IsProcessFailure(ex))
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
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var preferredLanguage = ConfigHasher.NormalizeAudioLanguage(configuration.PreferredAudioLanguage);
        var streamSelection = await GetAudioStreamSelectionAsync(
            episode,
            preferredLanguage,
            configuration.PreferAudioStreamWithMostChannels,
            cancellationToken).ConfigureAwait(false);
        var cacheVariant = streamSelection?.CacheVariant;

        // Rows written before stream selection existed came from FFmpeg's default stream, so
        // they are only reusable when that is still the effective stream.
        string? LegacyConfigHash(AnalysisMode rowMode) => streamSelection?.SelectsDefaultStream == true
            ? ConfigHasher.LegacyChromaprintCacheWithoutLanguage(configuration, rowMode)
            : null;

        // Resolve the stream before reading the cache so a language preference can reuse a fingerprint
        // generated with the same effective stream under the default selection.
        if (_cacheService.TryRead(episode.EpisodeId, cacheMode, CacheEntryType.Chromaprint, start, end, configuration, out uint[] cachedFingerprint, cacheVariant, LegacyConfigHash(cacheMode)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cachedFingerprint;
        }

        // A row under the mode's own key was written before the row was shared. Copy it under
        // the shared key so the next read is one lookup; the old row stays until its item is deleted.
        if (cacheMode != mode
            && _cacheService.TryRead(episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end, configuration, out cachedFingerprint, cacheVariant, LegacyConfigHash(mode)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _cacheService.Write(episode.EpisodeId, cacheMode, CacheEntryType.Chromaprint, start, end, configuration, cachedFingerprint, cacheVariant);
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
            throw new FingerprintException("chromaprint fingerprinting timed out", ex);
        }

        if (rawPoints.Length == 0 || rawPoints.Length % 4 != 0)
        {
            LogChromaprintReturnedPoints(_logger, rawPoints.Length, episode.Path);
            throw new FingerprintException("chromaprint output was malformed");
        }

        var results = MemoryMarshal.Cast<byte, uint>(rawPoints).ToArray();

        // Try to cache this fingerprint.
        cancellationToken.ThrowIfCancellationRequested();
        _cacheService.Write(episode.EpisodeId, cacheMode, CacheEntryType.Chromaprint, start, end, configuration, results, cacheVariant);

        return results;
    }

    // The most-channels policy is ffmpeg's own pick: the default-disposition stream, then most channels, then lowest index.
    private static (int Index, int Channels, string? Language, bool Default) SelectAudioStream(
        IReadOnlyList<(int Index, int Channels, string? Language, bool Default)> streams,
        bool preferMostChannels)
        => preferMostChannels
            ? streams.OrderByDescending(stream => stream.Default).ThenByDescending(stream => stream.Channels).ThenBy(stream => stream.Index).First()
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Hardware decode of \"{File}\" (id {Id}) exited with code {ExitCode}, decoding again in software")]
    private static partial void LogHardwareDecodeFailed(ILogger logger, string file, Guid id, int exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hardware decode of \"{File}\" (id {Id}) failed ({Reason}), decoding again in software")]
    private static partial void LogHardwareDecodeBroke(ILogger logger, string file, Guid id, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not probe \"{File}\" for hardware decode, scanning in software")]
    private partial void LogHardwareDecodeProbeFailed(Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chromaprint returned {Count} points for \"{Path}\"")]
    private static partial void LogChromaprintReturnedPoints(ILogger logger, int count, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to probe audio duration for {File}")]
    private static partial void LogAudioDurationProbeFailed(ILogger logger, Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to probe preferred audio language {Language} for {File}; using FFmpeg's default audio stream selection")]
    private static partial void LogPreferredAudioLanguageProbeFailed(ILogger logger, Exception ex, string file, string language);

    /// <summary>
    /// The audio stream a fingerprint is taken from.
    /// </summary>
    /// <param name="StreamIndex">Stream index to map explicitly, or <see langword="null"/> to leave FFmpeg's own pick unmapped.</param>
    /// <param name="CacheVariant">Effective stream identity that keys the fingerprint cache row.</param>
    /// <param name="SelectsDefaultStream">Whether the effective stream is FFmpeg's own pick, the stream rows from
    /// releases before stream selection hold. Also true where <paramref name="CacheVariant"/> names its index.</param>
    private sealed record AudioStreamSelection(int? StreamIndex, string CacheVariant, bool SelectsDefaultStream)
    {
        /// <summary>Cache variant of FFmpeg's own pick.</summary>
        public const string DefaultStreamCacheVariant = ConfigHasher.DefaultAudioStreamCacheVariant;
    }

    /// <summary>
    /// One input of a subtitle extraction run and the WebVTT files its streams decode to.
    /// </summary>
    /// <param name="Path">File ffmpeg reads: the media file, an ffconcat list over it (<paramref name="Concat"/>), or a sidecar or its UTF-8 copy.</param>
    /// <param name="Seek">Media second to seek to before reading, or <see langword="null"/> to read from the start.</param>
    /// <param name="Stop">Media second each output stops at with an output -t, or <see langword="null"/> to read to the end, or for a concat input to the list's outpoint.</param>
    /// <param name="Streams">Subtitle streams to decode, by stream index.</param>
    /// <param name="OutputPrefix">Path prefix of the output files; a stream's file adds its index and <c>.vtt</c>.</param>
    /// <param name="Concat">Whether <paramref name="Path"/> is an ffconcat list, read with the concat demuxer.</param>
    private sealed record SubtitleInput(string Path, double? Seek, double? Stop, int[] Streams, string OutputPrefix, bool Concat = false)
    {
        /// <summary>Gets the WebVTT file a stream decodes to.</summary>
        /// <param name="stream">The stream index.</param>
        /// <returns>The output file's path.</returns>
        public string OutputPath(int stream) => FormattableString.Invariant($"{OutputPrefix}-{stream}.vtt");
    }
}
