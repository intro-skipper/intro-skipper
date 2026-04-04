// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2022 nyanmisaka
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2025 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
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

    /// <summary>
    /// Fingerprint a queued episode.
    /// </summary>
    /// <param name="episode">Queued episode to fingerprint.</param>
    /// <param name="mode">Portion of media file to fingerprint. Introduction = first 25% / 10 minutes and Credits = last 4 minutes.</param>
    /// <returns>Numerical fingerprint points.</returns>
    public static uint[] Fingerprint(QueuedEpisode episode, AnalysisMode mode)
    {
        double start, end;

        if (mode == AnalysisMode.Introduction)
        {
            start = 0;
            end = episode.IntroFingerprintEnd;
        }
        else if (mode == AnalysisMode.Credits)
        {
            start = episode.CreditsFingerprintStart;
            end = episode.Duration;
        }
        else
        {
            throw new ArgumentException("Unknown analysis mode " + mode);
        }

        return Fingerprint(episode, mode, start, end);
    }

    /// <summary>
    /// Detect ranges of silence in the provided episode.
    /// </summary>
    /// <param name="episode">Queued episode.</param>
    /// <param name="range">Time range to search.</param>
    /// <returns>Array of TimeRange objects that are silent in the queued episode.</returns>
    public static TimeRange[] DetectSilence(QueuedEpisode episode, TimeRange range)
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

        var cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-silence-{1}-{2}-v3",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        var legacyCacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-silence-{1}-{2}-v2",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        if (ReadSilenceCache(cacheKey, out var cached) ||
            TryLoadLegacyCache(legacyCacheKey, cacheKey, raw => ParseSilenceRaw(raw, range.Start), static r => new TimeRange(r.ReadDouble(), r.ReadDouble()), WriteSilenceCache, out cached))
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
        WriteSilenceCache(cacheKey, result);

        return result;
    }

    /// <summary>
    /// Finds the location of all black frames in a media file within a time range.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="minimum">Percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <returns>Array of frames that are mostly black.</returns>
    public static BlackFrame[] DetectBlackFrames(
        QueuedEpisode episode,
        TimeRange range,
        int minimum,
        int threshold)
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

        var cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-blackframes-{1}-{2}-v2",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        var legacyCacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-blackframes-{1}-{2}-v1",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        if (ReadBlackFrameCache(cacheKey, out var cached) ||
            TryLoadLegacyCache(legacyCacheKey, cacheKey, static raw => ParseBlackFrame(raw), static r => new BlackFrame(r.ReadInt32(), r.ReadDouble(), r.ReadInt32()), WriteBlackFrameCache, out cached))
        {
            return [.. cached.Where(bf => bf.Percentage >= minimum)];
        }

        var raw = Encoding.UTF8.GetString(GetOutput(args, true));
        var allFrames = ParseBlackFrame(raw);
        WriteBlackFrameCache(cacheKey, allFrames);

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

        var cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-credits-blackframes-{1}-v2",
            episode.EpisodeId.ToString("N"),
            episode.CreditsFingerprintStart);

        var legacyCacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-blackframes-{1}-alt",
            episode.EpisodeId.ToString("N"),
            episode.CreditsFingerprintStart);

        if (ReadBlackFrameCache(cacheKey, out var cached) ||
            TryLoadLegacyCache(legacyCacheKey, cacheKey, static raw => ParseBlackFrame(raw), static r => new BlackFrame(r.ReadInt32(), r.ReadDouble(), r.ReadInt32()), WriteBlackFrameCache, out cached))
        {
            return cached;
        }

        var raw = Encoding.UTF8.GetString(GetOutput(args, true));
        var allFrames = ParseBlackFrame(raw);
        WriteBlackFrameCache(cacheKey, allFrames);

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

    private static BlackFrame[] ParseBlackFrame(string raw)
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
    /// <returns>Array of timestamps of key frames.</returns>
    public static double[] DetectKeyFrames(QueuedEpisode episode, TimeRange range)
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

        var cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-keyframes-{1}-{2}-v2",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        var legacyCacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-keyframes-{1}-{2}-v1",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        if (ReadKeyFrameCache(cacheKey, out var cached) ||
            TryLoadLegacyCache(legacyCacheKey, cacheKey, raw => ParseKeyFramesRaw(raw, range.Start), static r => r.ReadDouble(), WriteKeyFrameCache, out cached))
        {
            return cached;
        }

        var raw = Encoding.UTF8.GetString(GetOutput(args, stderr: true));
        var result = ParseKeyFramesRaw(raw, range.Start);
        WriteKeyFrameCache(cacheKey, result);

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
        if (LoadCachedFingerprint(episode, mode, out uint[] cachedFingerprint))
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
        CacheFingerprint(episode, mode, results);

        return [.. results];
    }

    /// <summary>
    /// Tries to load an episode's fingerprint from cache. If caching is not enabled, calling this function is a no-op.
    /// Tries the current binary format first, then migrates legacy text-format files on the fly.
    /// </summary>
    /// <param name="episode">Episode to try to load from cache.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="fingerprint">Array to store the fingerprint in.</param>
    /// <returns>true if the episode was successfully loaded from cache, false on any other error.</returns>
    private static bool LoadCachedFingerprint(
        QueuedEpisode episode,
        AnalysisMode mode,
        out uint[] fingerprint)
    {
        fingerprint = [];

        if (!IsCachingEnabled())
        {
            return false;
        }

        var id = episode.EpisodeId.ToString("N");
        var suffix = mode == AnalysisMode.Credits ? "-credits" : string.Empty;
        var cacheKey = id + suffix + "-chromaprint-v1";
        var legacyCacheKey = id + suffix;

        return ReadFingerprintCache(cacheKey, out fingerprint) ||
            TryLoadLegacyCache(legacyCacheKey, cacheKey, ParseFingerprintRaw, static r => r.ReadUInt32(), WriteFingerprintCache, out fingerprint);
    }

    /// <summary>
    /// Cache an episode's fingerprint to disk in binary format. If caching is not enabled, calling this function is a no-op.
    /// </summary>
    /// <param name="episode">Episode to store in cache.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="fingerprint">Fingerprint of the episode to store.</param>
    private static void CacheFingerprint(
        QueuedEpisode episode,
        AnalysisMode mode,
        List<uint> fingerprint)
    {
        var id = episode.EpisodeId.ToString("N");
        var suffix = mode == AnalysisMode.Credits ? "-credits" : string.Empty;
        WriteFingerprintCache(id + suffix + "-chromaprint-v1", [.. fingerprint]);
    }

    /// <summary>
    /// Remove a cached episode fingerprint from disk.
    /// </summary>
    /// <param name="id">Media item ID to remove from cache.</param>
    public static void DeleteFingerprintCache(Guid id)
    {
        // Delete from the SQLite cache database.
        using var db = Plugin.CreateCacheDb();
        db.DeleteByEpisodeId(id);

        // Also sweep any legacy binary files still on disk (pre-migration installs).
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
    /// Remove cached fingerprints from disk by mode.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    public static void DeleteCacheFiles(AnalysisMode mode)
    {
        // Delete from the SQLite cache database.
        using var db = Plugin.CreateCacheDb();
        db.DeleteByMode(mode);

        // Also sweep any legacy binary files still on disk (pre-migration installs).
        var cacheDir = Plugin.Instance?.FingerprintCachePath;
        if (cacheDir is not null && Directory.Exists(cacheDir))
        {
            foreach (var filePath in Directory.EnumerateFiles(cacheDir)
                .Where(f => mode == AnalysisMode.Introduction
                    ? !Path.GetFileName(f).Contains("-credits", StringComparison.OrdinalIgnoreCase)
                    : Path.GetFileName(f).Contains("-credits", StringComparison.OrdinalIgnoreCase)))
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
    /// Returns true if a fingerprint cache exists for the episode in either binary or legacy text format.
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>true if any fingerprint cache file exists.</returns>
    public static bool HasCachedFingerprint(QueuedEpisode episode, AnalysisMode mode)
    {
        if (!IsCachingEnabled())
        {
            return false;
        }

        var id = episode.EpisodeId.ToString("N");
        var suffix = mode == AnalysisMode.Credits ? "-credits" : string.Empty;

        using var db = Plugin.CreateCacheDb();
        return db.ExistsByKey(id + suffix + "-chromaprint-v1") ||
               File.Exists(GetLegacyFilePath(id + suffix + "-chromaprint-v1")) ||
               File.Exists(GetLegacyFilePath(id + suffix));
    }

    private static bool ReadFingerprintCache(string cacheKey, out uint[] result)
        => TryReadBinaryCache(cacheKey, static r => r.ReadUInt32(), out result);

    private static void WriteFingerprintCache(string cacheKey, uint[] fingerprint)
        => WriteBinaryCache(cacheKey, fingerprint, static (w, v) => w.Write(v));

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

    private static bool ReadSilenceCache(string cacheKey, out TimeRange[] result)
        => TryReadBinaryCache(cacheKey, static r => new TimeRange(r.ReadDouble(), r.ReadDouble()), out result);

    private static void WriteSilenceCache(string cacheKey, TimeRange[] ranges)
        => WriteBinaryCache(cacheKey, ranges, static (w, r) =>
        {
            w.Write(r.Start);
            w.Write(r.End);
        });

    private static bool ReadBlackFrameCache(string cacheKey, out BlackFrame[] result)
        => TryReadBinaryCache(cacheKey, static r => new BlackFrame(r.ReadInt32(), r.ReadDouble(), r.ReadInt32()), out result);

    private static void WriteBlackFrameCache(string cacheKey, BlackFrame[] frames)
        => WriteBinaryCache(cacheKey, frames, static (w, f) =>
        {
            w.Write(f.Percentage);
            w.Write(f.Time);
            w.Write(f.Frame);
        });

    private static bool ReadKeyFrameCache(string cacheKey, out double[] result)
        => TryReadBinaryCache(cacheKey, static r => r.ReadDouble(), out result);

    private static void WriteKeyFrameCache(string cacheKey, double[] timestamps)
        => WriteBinaryCache(cacheKey, timestamps, static (w, t) => w.Write(t));

    private static bool TryReadBinaryCache<T>(string cacheKey, Func<BinaryReader, T> deserializer, out T[] result)
    {
        result = [];

        if (!IsCachingEnabled())
        {
            return false;
        }

        try
        {
            using var db = Plugin.CreateCacheDb();
            if (!db.TryRead(cacheKey, out var data))
            {
                return false;
            }

            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            var count = reader.ReadInt32();
            var items = new T[count];
            for (var i = 0; i < count; i++)
            {
                items[i] = deserializer(reader);
            }

            if (Logger is { } logger)
            {
                LogDetectionCacheHit(logger, cacheKey);
            }

            result = items;
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or EndOfStreamException)
        {
            if (Logger is { } logger)
            {
                LogDetectionCacheReadError(logger, ex, cacheKey);
            }

            return false;
        }
    }

    private static void WriteBinaryCache<T>(string cacheKey, T[] items, Action<BinaryWriter, T> serializer)
    {
        if (!IsCachingEnabled())
        {
            return;
        }

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(items.Length);
        foreach (var item in items)
        {
            serializer(writer, item);
        }

        writer.Flush();
        using var db = Plugin.CreateCacheDb();
        db.Write(cacheKey, ms.ToArray());
    }

    private static bool TryLoadLegacyCache<T>(
        string legacyCacheKey,
        string newCacheKey,
        Func<string, T[]> rawParser,
        Func<BinaryReader, T> itemReader,
        Action<string, T[]> dbWriter,
        out T[] result)
    {
        result = [];

        if (!IsCachingEnabled())
        {
            return false;
        }

        // Phase 1: migrate old binary files from disk into SQLite.
        // The old binary filename matched newCacheKey exactly (before SQLite was introduced).
        var legacyBinaryPath = GetLegacyFilePath(newCacheKey);
        if (File.Exists(legacyBinaryPath))
        {
            try
            {
                using var fs = File.Open(legacyBinaryPath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(fs);
                var count = reader.ReadInt32();
                var items = new T[count];
                for (var i = 0; i < count; i++)
                {
                    items[i] = itemReader(reader);
                }

                if (items.Length > 0)
                {
                    if (Logger is { } migLogger)
                    {
                        LogMigratingLegacyCache(migLogger, newCacheKey, "DB");
                    }

                    dbWriter(newCacheKey, items);
                    result = items;

                    try
                    {
                        File.Delete(legacyBinaryPath);
                    }
                    catch (IOException ex)
                    {
                        if (Logger is { } deleteLogger)
                        {
                            LogFailedToDeleteCorruptLegacyCache(deleteLogger, ex, legacyBinaryPath);
                        }
                    }

                    return true;
                }
                else
                {
                    // Empty binary file — best-effort delete and fall through to Phase 2.
                    try
                    {
                        File.Delete(legacyBinaryPath);
                    }
                    catch (IOException deleteEx)
                    {
                        if (Logger is { } migLogger)
                        {
                            LogFailedToDeleteCorruptLegacyCache(migLogger, deleteEx, legacyBinaryPath);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                // Corrupt or unreadable binary file — delete it and fall through to text-format check.
                try
                {
                    File.Delete(legacyBinaryPath);
                }
                catch (IOException deleteEx)
                {
                    // Best-effort cleanup; failure to delete should not stop migration, but log for diagnostics.
                    if (Logger is { } migLogger)
                    {
                        LogFailedToDeleteCorruptLegacyCache(migLogger, deleteEx, legacyBinaryPath);
                    }
                }
            }
        }

        // Phase 2: migrate even-older text-format files from disk into SQLite.
        // Only fingerprints had a text format; other detection types had a versioned binary format
        // (e.g. silence-v2) which is handled by re-analysis if missing.
        var legacyTextPath = GetLegacyFilePath(legacyCacheKey);
        try
        {
            var raw = File.ReadAllText(legacyTextPath, Encoding.UTF8);
            result = rawParser(raw);

            // If the parser returned nothing the legacy file is corrupt or unreadable.
            // Delete it so it doesn't block future attempts, then fall through to re-analysis.
            if (result.Length == 0)
            {
                File.Delete(legacyTextPath);
                return false;
            }

            if (Logger is { } logger)
            {
                LogMigratingLegacyCache(logger, legacyCacheKey, newCacheKey);
            }

            dbWriter(newCacheKey, result);
            File.Delete(legacyTextPath);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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

    [LoggerMessage(Level = LogLevel.Error, Message = "I/O error while reading fingerprint cache from {Path}")]
    private static partial void LogFingerprintCacheReadIoError(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Access error while reading fingerprint cache from {Path}")]
    private static partial void LogFingerprintCacheReadAccessError(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Invalid fingerprint entry '{RawNumber}' found in cache for {Path} ({Id}), ignoring cache")]
    private static partial void LogInvalidFingerprintEntry(ILogger logger, string rawNumber, string path, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "DeleteEpisodeCache {FilePath}")]
    private static partial void LogDeleteEpisodeCache(ILogger logger, string filePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete legacy cache file '{FilePath}'")]
    private static partial void LogDeleteLegacyCacheFileFailed(ILogger logger, Exception ex, string filePath);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Detection cache hit for {CacheKey}")]
    private static partial void LogDetectionCacheHit(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error reading detection cache from {Path}")]
    private static partial void LogDetectionCacheReadError(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Migrating legacy cache {LegacyKey} to {NewKey}")]
    private static partial void LogMigratingLegacyCache(ILogger logger, string legacyKey, string newKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to delete corrupt legacy cache file at path '{LegacyPath}'")]
    private static partial void LogFailedToDeleteCorruptLegacyCache(ILogger logger, Exception ex, string legacyPath);

    [GeneratedRegex("silence_(?<type>start|end): (?<time>[0-9\\.]+)")]
    private static partial Regex SilenceRegex();

    [GeneratedRegex(@"\[Parsed_blackframe_0 @ [^\]]+\] frame:(\d+) pblack:(\d+) .*? t:([\d.]+)")]
    private static partial Regex BlackFrameRegex();
}
