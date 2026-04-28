// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2022 nyanmisaka
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2025 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper;

/// <summary>
/// Wrapper for libchromaprint and the silencedetect filter.
/// </summary>
public static partial class FFmpegWrapper
{
    /// <summary>
    /// Used with FFmpeg's silencedetect filter to extract the start and end times of silence.
    /// </summary>
    private static readonly Regex _silenceDetectionExpression = SilenceRegex();

    /// <summary>
    /// Used with FFmpeg's blackframe filter to extract the time and percentage of black pixels.
    /// </summary>
    private static readonly Regex _blackFrameRegex = BlackFrameRegex();

    /// <summary>
    /// Gets or sets the logger.
    /// </summary>
    public static ILogger? Logger { get; set; }

    private static Dictionary<string, string> ChromaprintLogs { get; set; } = [];

    private static bool IsCachingEnabled()
        => Plugin.Instance?.Configuration.CacheFingerprints ?? false;

    private static string GetLegacyFilePath(string cacheKey)
        => Path.Join(Plugin.Instance?.FingerprintCachePath ?? string.Empty, cacheKey);

    /// <summary>
    /// Check that the installed version of ffmpeg supports chromaprint.
    /// </summary>
    /// <returns>true if a compatible version of ffmpeg is installed, false on any error.</returns>
    public static bool CheckFFmpegVersion()
    {
        try
        {
            // Always log ffmpeg's version information.
            if (!CheckFFmpegRequirement(
                "-version",
                "ffmpeg",
                "version",
                "Unknown error with FFmpeg version"))
            {
                ChromaprintLogs["error"] = "unknown_error";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            // First, validate that the installed version of ffmpeg supports chromaprint at all.
            if (!CheckFFmpegRequirement(
                "-muxers",
                "chromaprint",
                "muxer list",
                "The installed version of ffmpeg does not support chromaprint"))
            {
                ChromaprintLogs["error"] = "chromaprint_not_supported";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            // Second, validate that the Chromaprint muxer understands the "-fp_format raw" option.
            if (!CheckFFmpegRequirement(
                "-h muxer=chromaprint",
                "binary raw fingerprint",
                "chromaprint options",
                "The installed version of ffmpeg does not support raw binary fingerprints"))
            {
                ChromaprintLogs["error"] = "fp_format_not_supported";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            // Third, validate that ffmpeg supports of the all required silencedetect options.
            if (!CheckFFmpegRequirement(
                "-h filter=silencedetect",
                "noise tolerance",
                "silencedetect options",
                "The installed version of ffmpeg does not support the silencedetect filter"))
            {
                ChromaprintLogs["error"] = "silencedetect_not_supported";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            if (Logger is { } logger)
            {
                LogFfmpegVersionValid(logger);
            }

            ChromaprintLogs["error"] = "okay";
            return true;
        }
        catch
        {
            ChromaprintLogs["error"] = "unknown_error";
            WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
            return false;
        }
    }

    private static (double Start, double End) GetFingerprintRange(QueuedEpisode episode, AnalysisMode mode)
    {
        return mode switch
        {
            AnalysisMode.Introduction => (0, episode.IntroFingerprintEnd),
            AnalysisMode.Credits => (episode.CreditsFingerprintStart, episode.Duration),
            _ => throw new ArgumentException("Unknown analysis mode " + mode),
        };
    }

    /// <summary>
    /// Fingerprint a queued episode.
    /// </summary>
    /// <param name="episode">Queued episode to fingerprint.</param>
    /// <param name="mode">Portion of media file to fingerprint. Introduction = first 25% / 10 minutes and Credits = last 4 minutes.</param>
    /// <returns>Numerical fingerprint points.</returns>
    public static uint[] Fingerprint(QueuedEpisode episode, AnalysisMode mode)
    {
        var (start, end) = GetFingerprintRange(episode, mode);
        return Fingerprint(episode, mode, start, end);
    }

    /// <summary>
    /// Detect ranges of silence in the provided episode.
    /// </summary>
    /// <param name="episode">Queued episode.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="mode">Analysis mode, used to correctly key the cache entry.</param>
    /// <returns>Array of TimeRange objects that are silent in the queued episode.</returns>
    public static TimeRange[] DetectSilence(QueuedEpisode episode, TimeRange range, AnalysisMode mode)
    {
        if (Logger is { } detectLogger)
        {
            LogDetectingSilence(detectLogger, episode.Path, range.Start, range.End, episode.EpisodeId);
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

        var legacyCacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-silence-{1}-{2}-v2",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        if (TryReadJsonCache(episode.EpisodeId, mode, CacheEntryType.Silence, range.Start, range.End, out TimeRange[] cached) ||
            TryLoadLegacyCache(legacyCacheKey, episode.EpisodeId, mode, CacheEntryType.Silence, range.Start, range.End, raw => ParseSilenceRaw(raw, range.Start), out cached))
        {
            return cached;
        }

        /* Each match will have a type (either "start" or "end") and a timecode (a double).
         *
         * Sample output:
         * [silencedetect @ 0x000000000000] silence_start: 12.34
         * [silencedetect @ 0x000000000000] silence_end: 56.123 | silence_duration: 43.783
        */
        var raw = Encoding.UTF8.GetString(GetOutput(args, true));
        var result = ParseSilenceRaw(raw, range.Start);
        WriteJsonCache(episode.EpisodeId, mode, CacheEntryType.Silence, range.Start, range.End, result);

        return result;
    }

    /// <summary>
    /// Finds the location of all black frames in a media file within a time range.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="minimum">Percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="mode">Analysis mode, used to correctly key the cache entry.</param>
    /// <returns>Array of frames that are mostly black.</returns>
    public static BlackFrame[] DetectBlackFrames(
        QueuedEpisode episode,
        TimeRange range,
        int minimum,
        int threshold,
        AnalysisMode mode)
    {
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

        var legacyCacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-blackframes-{1}-{2}-v1",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        if (TryReadJsonCache(episode.EpisodeId, mode, CacheEntryType.BlackFrame, range.Start, range.End, out BlackFrame[] cached) ||
            TryLoadLegacyCache(legacyCacheKey, episode.EpisodeId, mode, CacheEntryType.BlackFrame, range.Start, range.End, static raw => ParseBlackFrame(raw), out cached))
        {
            return [.. cached.Where(bf => bf.Percentage >= minimum)];
        }

        var raw = Encoding.UTF8.GetString(GetOutput(args, true));
        var allFrames = ParseBlackFrame(raw);
        WriteJsonCache(episode.EpisodeId, mode, CacheEntryType.BlackFrame, range.Start, range.End, allFrames);

        return [.. allFrames.Where(bf => bf.Percentage >= minimum)];
    }

    /// <summary>
    /// Finds the location of all black frames in a media file starting at a given time.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <returns>Array of frames that are mostly black.</returns>
    public static BlackFrame[] DetectBlackFrames(QueuedEpisode episode, int threshold)
    {
        ArgumentNullException.ThrowIfNull(episode);

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

        var legacyCacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-blackframes-{1}-alt",
            episode.EpisodeId.ToString("N"),
            episode.CreditsFingerprintStart);

        if (TryReadJsonCache(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackFrame, episode.CreditsFingerprintStart, 0, out BlackFrame[] cached) ||
            TryLoadLegacyCache(legacyCacheKey, episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackFrame, episode.CreditsFingerprintStart, 0, static raw => ParseBlackFrame(raw), out cached))
        {
            return cached;
        }

        var raw = Encoding.UTF8.GetString(GetOutput(args, true));
        var allFrames = ParseBlackFrame(raw);
        WriteJsonCache(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackFrame, episode.CreditsFingerprintStart, 0, allFrames);

        return allFrames;
    }

    private static TimeRange[] ParseSilenceRaw(string raw, double rangeStart)
    {
        var currentRange = new TimeRange();
        var silenceRanges = new List<TimeRange>();

        foreach (Match match in _silenceDetectionExpression.Matches(raw))
        {
            var isStart = match.Groups["type"].Value == "start";
            var time = Convert.ToDouble(match.Groups["time"].Value, CultureInfo.InvariantCulture);

            if (isStart)
            {
                currentRange.Start = time + rangeStart;
            }
            else
            {
                currentRange.End = time + rangeStart;
                silenceRanges.Add(new TimeRange(currentRange));
            }
        }

        return [.. silenceRanges];
    }

    private static double[] ParseKeyFramesRaw(string raw, double rangeStart)
    {
        var keyframes = new List<double>();

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var ptsIndex = line.IndexOf("pts_time:", StringComparison.OrdinalIgnoreCase);
            if (ptsIndex == -1)
            {
                continue;
            }

            var ptsTimeStr = line[(ptsIndex + 9)..].Split(' ', 2)[0];

            if (double.TryParse(ptsTimeStr, CultureInfo.InvariantCulture, out double timestamp))
            {
                keyframes.Add(timestamp + rangeStart);
            }
            else
            {
                if (Logger is { } parseLogger)
                {
                    LogFailedToParseTimestamp(parseLogger, ptsTimeStr, line);
                }
            }
        }

        return [.. keyframes];
    }

    internal static BlackFrame[] ParseBlackFrame(string raw)
    {
        var blackFrames = new List<BlackFrame>();
        /* Run the blackframe filter.
         *
         * Sample output:
         * [Parsed_blackframe_0 @ 0x0000000] frame:1 pblack:99 pts:43 t:0.043000 type:B last_keyframe:0
         * [Parsed_blackframe_0 @ 0x0000000] frame:2 pblack:99 pts:85 t:0.085000 type:B last_keyframe:0
         */
        foreach (var line in raw.Split('\n'))
        {
            var match = _blackFrameRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var frame = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var percentage = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var time = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

            blackFrames.Add(new BlackFrame(percentage, time, frame));
        }

        return [.. blackFrames];
    }

    /// <summary>
    /// Detects key frames in a media file within a time range.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="mode">Analysis mode, used to correctly key the cache entry.</param>
    /// <returns>Array of timestamps of key frames.</returns>
    public static double[] DetectKeyFrames(QueuedEpisode episode, TimeRange range, AnalysisMode mode)
    {
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

        var legacyCacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-keyframes-{1}-{2}-v1",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        if (TryReadJsonCache(episode.EpisodeId, mode, CacheEntryType.Keyframe, range.Start, range.End, out double[] cached) ||
            TryLoadLegacyCache(legacyCacheKey, episode.EpisodeId, mode, CacheEntryType.Keyframe, range.Start, range.End, raw => ParseKeyFramesRaw(raw, range.Start), out cached))
        {
            return cached;
        }

        var raw = Encoding.UTF8.GetString(GetOutput(args, stderr: true));
        var result = ParseKeyFramesRaw(raw, range.Start);
        WriteJsonCache(episode.EpisodeId, mode, CacheEntryType.Keyframe, range.Start, range.End, result);

        return result;
    }

    /// <summary>
    /// Gets Chromaprint debugging logs.
    /// </summary>
    /// <returns>Markdown formatted logs.</returns>
    public static string GetChromaprintLogs()
    {
        // Print the FFmpeg detection status at the top.
        // Format: "* FFmpeg: `error`"
        // Append two newlines to separate the bulleted list from the logs
        var logs = string.Format(
            CultureInfo.InvariantCulture,
            "* FFmpeg: `{0}`\n\n",
            ChromaprintLogs["error"]);

        // Always include ffmpeg version information
        logs += FormatFFmpegLog("version");

        // Include feature detection logs to verify no warnings
        foreach (var kvp in ChromaprintLogs.Where(kvp => kvp.Key is not "error" and not "version"))
        {
            logs += FormatFFmpegLog(kvp.Key);
        }

        return logs;
    }

    /// <summary>
    /// Run an FFmpeg command with the provided arguments and validate that the output contains
    /// the provided string.
    /// </summary>
    /// <param name="arguments">Arguments to pass to FFmpeg.</param>
    /// <param name="mustContain">String that the output must contain. Case-insensitive.</param>
    /// <param name="bundleName">Support bundle key to store FFmpeg's output under.</param>
    /// <param name="errorMessage">Error message to log if this requirement is not met.</param>
    /// <returns>true on success, false on error.</returns>
    private static bool CheckFFmpegRequirement(
        string arguments,
        string mustContain,
        string bundleName,
        string errorMessage)
    {
        var requirementLogger = Logger;
        if (requirementLogger is not null)
        {
            LogCheckingRequirement(requirementLogger, arguments);
        }

        var output = Encoding.UTF8.GetString(GetOutput(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), false, 2000));
        if (requirementLogger is not null)
        {
            LogFfmpegOutput(requirementLogger, arguments, output);
        }

        ChromaprintLogs[bundleName] = output;

        if (!output.Contains(mustContain, StringComparison.OrdinalIgnoreCase))
        {
            if (requirementLogger is not null)
            {
                LogFfmpegRequirementFailed(requirementLogger, errorMessage);
            }

            return false;
        }

        if (requirementLogger is not null)
        {
            LogFfmpegRequirementMet(requirementLogger, arguments);
        }

        return true;
    }

    /// <summary>
    /// Runs ffmpeg and returns standard output (or error).
    /// </summary>
    /// <param name="args">Arguments to pass to ffmpeg as individual tokens.</param>
    /// <param name="stderr">If standard error should be returned.</param>
    /// <param name="timeout">Timeout (in miliseconds) to wait for ffmpeg to exit.</param>
    private static ReadOnlySpan<byte> GetOutput(
        IReadOnlyList<string> args,
        bool stderr = false,
        int timeout = 60 * 1000)
    {
        var ffmpegPath = Plugin.Instance?.FFmpegPath ?? "ffmpeg";

        // The silencedetect and blackframe filters output data at the info log level.
        var useInfoLevel = args.Any(a =>
            a.Contains("silencedetect", StringComparison.OrdinalIgnoreCase) ||
            a.Contains("blackframe", StringComparison.OrdinalIgnoreCase) ||
            a.Contains("showinfo", StringComparison.OrdinalIgnoreCase));

        var logLevel = useInfoLevel ? "info" : "warning";

        // For FFmpeg info queries (-version, -muxers, -h), don't add the thread count flag
        // to avoid "Trailing option(s) found" warning. These are quick queries.
        var firstArg = args.Count > 0 ? args[0] : string.Empty;
        var isInfoQuery = firstArg.StartsWith("-version", StringComparison.Ordinal) ||
            firstArg.StartsWith("-muxers", StringComparison.Ordinal) ||
            firstArg.StartsWith("-h", StringComparison.Ordinal);

        var info = new ProcessStartInfo(ffmpegPath)
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            ErrorDialog = false,
            RedirectStandardOutput = !stderr,
            RedirectStandardError = stderr
        };

        // Prepend flags to suppress FFmpeg banner and set log level / thread count.
        info.ArgumentList.Add("-hide_banner");
        if (!isInfoQuery)
        {
            info.ArgumentList.Add("-threads");
            info.ArgumentList.Add((Plugin.Instance?.Configuration.ProcessThreads ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        info.ArgumentList.Add("-loglevel");
        info.ArgumentList.Add(logLevel);

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var ffmpeg = new Process { StartInfo = info };
        if (Logger is { } startLogger)
        {
            LogStartingFfmpeg(startLogger, string.Join(" ", info.ArgumentList));
        }

        ffmpeg.Start();

        try
        {
            ffmpeg.PriorityClass = Plugin.Instance?.Configuration.ProcessPriority ?? ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e)
        {
            if (Logger is { } priorityLogger)
            {
                LogFfmpegPriorityNotModified(priorityLogger, e.Message);
            }
        }

        using var ms = new MemoryStream();
        var buf = new byte[4096];

        using (var streamReader = stderr ? ffmpeg.StandardError : ffmpeg.StandardOutput)
        {
            int bytesRead;
            while ((bytesRead = streamReader.BaseStream.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, bytesRead);
            }
        }

        ffmpeg.WaitForExit(timeout);

        return ms.ToArray();
    }

    /// <summary>
    /// Fingerprint a queued episode.
    /// </summary>
    /// <param name="episode">Queued episode to fingerprint.</param>
    /// <param name="mode">Portion of media file to fingerprint.</param>
    /// <param name="start">Time (in seconds) relative to the start of the file to start fingerprinting from.</param>
    /// <param name="end">Time (in seconds) relative to the start of the file to stop fingerprinting at.</param>
    /// <returns>Numerical fingerprint points.</returns>
    private static uint[] Fingerprint(QueuedEpisode episode, AnalysisMode mode, double start, double end)
    {
        // Try to load this episode from cache before running ffmpeg.
        if (LoadCachedFingerprint(episode, mode, start, end, out uint[] cachedFingerprint))
        {
            if (Logger is { } cacheLogger)
            {
                LogFingerprintCacheHit(cacheLogger, episode.Path);
            }

            return cachedFingerprint;
        }

        if (Logger is { } fingerprintLogger)
        {
            LogFingerprinting(fingerprintLogger, start, end, episode.Path, episode.EpisodeId);
        }

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
        var rawPoints = GetOutput(args);
        if (rawPoints.Length == 0 || rawPoints.Length % 4 != 0)
        {
            if (Logger is { } chromaLogger)
            {
                LogChromaprintReturnedPoints(chromaLogger, rawPoints.Length, episode.Path);
            }

            throw new FingerprintException("chromaprint output for \"" + episode.Path + "\" was malformed");
        }

        var results = new List<uint>();
        for (var i = 0; i < rawPoints.Length; i += 4)
        {
            var rawPoint = rawPoints.Slice(i, 4);
            results.Add(BitConverter.ToUInt32(rawPoint));
        }

        // Try to cache this fingerprint.
        WriteJsonCache(episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end, [.. results]);

        return [.. results];
    }

    /// <summary>
    /// Tries to load an episode's fingerprint from cache. If caching is not enabled, calling this function is a no-op.
    /// Checks the SQLite detection cache first, then migrates legacy on-disk files on the fly.
    /// </summary>
    /// <param name="episode">Episode to try to load from cache.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="start">Start time (in seconds) used when the fingerprint was cached.</param>
    /// <param name="end">End time (in seconds) used when the fingerprint was cached.</param>
    /// <param name="fingerprint">Array to store the fingerprint in.</param>
    /// <returns><c>true</c> if the episode was successfully loaded from cache; otherwise <c>false</c>.</returns>
    private static bool LoadCachedFingerprint(
        QueuedEpisode episode,
        AnalysisMode mode,
        double start,
        double end,
        out uint[] fingerprint)
    {
        fingerprint = [];

        if (!IsCachingEnabled())
        {
            return false;
        }

        var id = episode.EpisodeId.ToString("N");
        var suffix = mode == AnalysisMode.Credits ? "-credits" : string.Empty;
        var legacyCacheKey = id + suffix;

        return TryReadJsonCache(episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end, out fingerprint) ||
            TryLoadLegacyCache(legacyCacheKey, episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end, ParseFingerprintRaw, out fingerprint);
    }

    /// <summary>
    /// Remove a cached episode fingerprint from the database.
    /// </summary>
    /// <param name="id">Media item ID to remove from cache.</param>
    public static void DeleteFingerprintCache(Guid id)
    {
        // Delete from the SQLite cache database.
        using var db = Plugin.CreateCacheDbContext();
        db.DetectionCache.Where(e => e.ItemId == id).ExecuteDelete();

        var cacheDir = Plugin.Instance?.FingerprintCachePath;
        if (cacheDir is not null && Directory.Exists(cacheDir))
        {
            var filePattern = id.ToString("N") + "*";
            foreach (var filePath in Directory.EnumerateFiles(cacheDir, filePattern))
            {
                if (Logger is { } deleteLogger)
                {
                    LogDeleteEpisodeCache(deleteLogger, filePath);
                }

                try
                {
                    File.Delete(filePath);
                }
                catch (IOException ex)
                {
                    if (Logger is { } errLogger)
                    {
                        LogDeleteLegacyCacheFileFailed(errLogger, ex, filePath);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Remove cached fingerprints from the database by mode.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    public static void DeleteCacheFiles(AnalysisMode mode)
    {
        // Delete from the SQLite cache database.
        using var db = Plugin.CreateCacheDbContext();
        db.DetectionCache.Where(e => e.Mode == mode).ExecuteDelete();

        var cacheDir = Plugin.Instance?.FingerprintCachePath;
        if (cacheDir is not null && Directory.Exists(cacheDir))
        {
            foreach (var filePath in Directory.EnumerateFiles(cacheDir)
                .Where(f => mode == AnalysisMode.Introduction
                    ? !Path.GetFileName(f).Contains("credit", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(f).Contains("blackframes", StringComparison.OrdinalIgnoreCase)
                    : Path.GetFileName(f).Contains("credit", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(f).Contains("blackframes", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (IOException ex)
                {
                    if (Logger is { } errLogger)
                    {
                        LogDeleteLegacyCacheFileFailed(errLogger, ex, filePath);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> if a fingerprint cache entry exists for the episode in the SQLite cache or as a legacy on-disk text file.
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns><c>true</c> if any fingerprint cache entry exists; otherwise <c>false</c>.</returns>
    public static bool HasCachedFingerprint(QueuedEpisode episode, AnalysisMode mode)
    {
        if (!IsCachingEnabled())
        {
            return false;
        }

        var (start, end) = GetFingerprintRange(episode, mode);

        try
        {
            using var db = Plugin.CreateCacheDbContext();
            if (db.DetectionCache.Any(e =>
                e.ItemId == episode.EpisodeId &&
                e.Mode == mode &&
                e.Type == CacheEntryType.Chromaprint &&
                e.Start == start &&
                e.End == end))
            {
                return true;
            }
        }
        catch (DbException ex)
        {
            if (Logger is { } logger)
            {
                LogDetectionCacheReadError(logger, ex, $"{episode.EpisodeId:N}-{mode}-{CacheEntryType.Chromaprint}");
            }
        }

        var id = episode.EpisodeId.ToString("N");
        var suffix = mode == AnalysisMode.Credits ? "-credits" : string.Empty;
        var legacyPath = GetLegacyFilePath(id + suffix);

        return File.Exists(legacyPath);
    }

    private static bool TryReadJsonCache<T>(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, out T[] result)
    {
        result = [];

        if (!IsCachingEnabled())
        {
            return false;
        }

        try
        {
            using var db = Plugin.CreateCacheDbContext();

            // NOTE: Start/End are compared with == which is safe only because the exact same
            // double values that were written are used for lookup (no intermediate arithmetic).
            // If a future caller computes start/end differently, the lookup will silently miss.
            var entry = db.DetectionCache
                .FirstOrDefault(e => e.ItemId == itemId && e.Mode == mode && e.Type == type && e.Start == start && e.End == end);

            if (entry is null)
            {
                return false;
            }

            var json = DecompressBrotli<T[]>(entry.Data);
            result = json ?? [];

            if (Logger is { } logger)
            {
                LogDetectionCacheHit(logger, $"{itemId:N}-{mode}-{type}");
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidDataException or DbException)
        {
            if (Logger is { } logger)
            {
                LogDetectionCacheReadError(logger, ex, $"{itemId:N}-{mode}-{type}");
            }

            return false;
        }
    }

    private static void WriteJsonCache<T>(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, T[] items)
    {
        if (!IsCachingEnabled())
        {
            return;
        }

        var data = CompressBrotli(items);

        try
        {
            using var db = Plugin.CreateCacheDbContext();

            // NOTE: Start/End are compared with == which is safe only because the exact same
            // double values that were written are used for lookup (no intermediate arithmetic).
            var existing = db.DetectionCache
                .FirstOrDefault(e => e.ItemId == itemId && e.Mode == mode && e.Type == type && e.Start == start && e.End == end);

            if (existing is not null)
            {
                existing.Data = data;
            }
            else
            {
                db.DetectionCache.Add(new DbDetectionCache(itemId, mode, type, data, start, end));
            }

            db.SaveChanges();
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            if (Logger is { } logger)
            {
                LogDetectionCacheWriteError(logger, ex, $"{itemId:N}-{mode}-{type}");
            }

            // Suppress duplicate-insert races and database-level cache failures. The cache is a
            // performance optimization; write failures should never discard valid analysis results.
        }
    }

    private static uint[] ParseFingerprintRaw(string raw)
    {
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<uint>(lines.Length);
        foreach (var line in lines)
        {
            if (!uint.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                // Any invalid entry means the file is corrupt — abort so FFmpeg re-analyzes.
                return [];
            }

            result.Add(value);
        }

        return [.. result];
    }

    private static bool TryLoadLegacyCache<T>(
        string legacyCacheKey,
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end,
        Func<string, T[]> rawParser,
        out T[] result)
    {
        result = [];

        if (!IsCachingEnabled())
        {
            return false;
        }

        // Migrate legacy on-disk text files into the SQLite cache.
        var legacyTextPath = GetLegacyFilePath(legacyCacheKey);
        if (!File.Exists(legacyTextPath))
        {
            return false;
        }

        try
        {
            var raw = File.ReadAllText(legacyTextPath, Encoding.UTF8);
            result = rawParser(raw);

            // An empty chromaprint legacy file is corrupt; empty detection result caches are valid.
            if (type == CacheEntryType.Chromaprint && result.Length == 0)
            {
                File.Delete(legacyTextPath);
                return false;
            }

            // Crosscheck chromaprint fingerprint duration against current settings.
            if (type == CacheEntryType.Chromaprint)
            {
                var inferredDuration = ChromaprintConstants.InferDuration(result.Length);
                var expectedDuration = Math.Round(end - start);

                if (Math.Abs(inferredDuration - expectedDuration) > ChromaprintConstants.DurationTolerance)
                {
                    if (Logger is { } mismatchLogger)
                    {
                        LogLegacyDurationMismatch(mismatchLogger, legacyCacheKey, inferredDuration, expectedDuration);
                    }

                    File.Delete(legacyTextPath);
                    return false;
                }
            }

            if (Logger is { } logger)
            {
                LogMigratingLegacyCache(logger, legacyCacheKey, $"{itemId:N}-{mode}-{type}");
            }

            WriteJsonCache(itemId, mode, type, start, end, result);
            File.Delete(legacyTextPath);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // FileNotFoundException is a subclass of IOException and is the normal case when the
            // legacy text file simply does not exist — suppress it silently to avoid log noise.
            if (ex is not FileNotFoundException && Logger is { } logger)
            {
                LogDetectionCacheReadError(logger, ex, legacyTextPath);
            }

            return false;
        }
    }

    private static string FormatFFmpegLog(string key)
    {
        /* Format:
        * FFmpeg NAME:
        * ```
        * LOGS
        * ```
        */

        var formatted = string.Format(CultureInfo.InvariantCulture, "FFmpeg {0}:\n```\n", key);
        formatted += ChromaprintLogs[key];

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

    [LoggerMessage(Level = LogLevel.Trace, Message = "Detecting silence in \"{File}\" (range {Start}-{End}, id {Id})")]
    private static partial void LogDetectingSilence(ILogger logger, string file, double start, double end, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse timestamp: {PtsTimeStr} from line: {Line}")]
    private static partial void LogFailedToParseTimestamp(ILogger logger, string ptsTimeStr, string line);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Checking FFmpeg requirement {Arguments}")]
    private static partial void LogCheckingRequirement(ILogger logger, string arguments);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Output of ffmpeg {Arguments}: {Output}")]
    private static partial void LogFfmpegOutput(ILogger logger, string arguments, string output);

    [LoggerMessage(Level = LogLevel.Error, Message = "{ErrorMessage}")]
    private static partial void LogFfmpegRequirementFailed(ILogger logger, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FFmpeg requirement {Arguments} met")]
    private static partial void LogFfmpegRequirementMet(ILogger logger, string arguments);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting ffmpeg with the following arguments: {Arguments}")]
    private static partial void LogStartingFfmpeg(ILogger logger, string arguments);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ffmpeg priority could not be modified. {Message}")]
    private static partial void LogFfmpegPriorityNotModified(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Fingerprint cache hit on {File}")]
    private static partial void LogFingerprintCacheHit(ILogger logger, string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fingerprinting [{Start}, {End}] from \"{File}\" (id {Id})")]
    private static partial void LogFingerprinting(ILogger logger, double start, double end, string file, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chromaprint returned {Count} points for \"{Path}\"")]
    private static partial void LogChromaprintReturnedPoints(ILogger logger, int count, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "DeleteEpisodeCache {FilePath}")]
    private static partial void LogDeleteEpisodeCache(ILogger logger, string filePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete legacy cache file '{FilePath}'")]
    private static partial void LogDeleteLegacyCacheFileFailed(ILogger logger, Exception ex, string filePath);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Detection cache hit for {CacheKey}")]
    private static partial void LogDetectionCacheHit(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error reading detection cache from {Path}")]
    private static partial void LogDetectionCacheReadError(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error writing detection cache for {CacheKey}")]
    private static partial void LogDetectionCacheWriteError(ILogger logger, Exception ex, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Migrating legacy cache {LegacyKey} to {NewKey}")]
    private static partial void LogMigratingLegacyCache(ILogger logger, string legacyKey, string newKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Legacy fingerprint {CacheKey} duration mismatch (inferred {Inferred}s vs expected {Expected}s), re-fingerprinting")]
    private static partial void LogLegacyDurationMismatch(ILogger logger, string cacheKey, double inferred, double expected);

    [GeneratedRegex("silence_(?<type>start|end): (?<time>[0-9\\.]+)")]
    private static partial Regex SilenceRegex();

    /// <summary>
    /// Serializes and compresses a value using Brotli compression.
    /// </summary>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="value">The value to serialize and compress.</param>
    /// <returns>The Brotli-compressed data.</returns>
    internal static byte[] CompressBrotli<T>(T value)
    {
        var level = Plugin.Instance?.Configuration.CacheCompressionLevel ?? CompressionLevel.Optimal;
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, level))
        {
            JsonSerializer.Serialize(brotli, value);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decompresses and deserializes Brotli-compressed data.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to.</typeparam>
    /// <param name="compressed">The Brotli-compressed data.</param>
    /// <returns>The deserialized value, or the default if deserialization returns null.</returns>
    internal static T? DecompressBrotli<T>(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(brotli);
    }

    [GeneratedRegex(@"\[Parsed_blackframe_0 @ [^\]]+\] frame:(\d+) pblack:(\d+) .*? t:([\d.]+)")]
    private static partial Regex BlackFrameRegex();
}
