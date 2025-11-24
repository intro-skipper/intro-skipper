// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper;

/// <summary>
/// Wrapper for libchromaprint and the silencedetect filter.
/// </summary>
public static partial class FFmpegWrapper
{
    private const uint FingerprintCacheMagic = 0x49464348; // IFCH -> Intro Fingerprint Cache
    private const ushort FingerprintCacheVersion = 1;
    private const string FingerprintCacheExtension = ".ifch";
    private const uint BlackFrameCacheMagic = 0x49464243; // IFBC -> Intro BlackFrame Cache
    private const ushort BlackFrameCacheVersion = 1;
    private const uint SilenceCacheMagic = 0x49465343; // IFSC -> Intro Silence Cache
    private const ushort SilenceCacheVersion = 1;
    private const string BlackFrameCacheExtension = ".ifbc";
    private const string SilenceCacheExtension = ".ifsc";

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

            Logger?.LogDebug("Installed version of ffmpeg meets fingerprinting requirements");
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
        Logger?.LogTrace(
            "Detecting silence in \"{File}\" (range {Start}-{End}, id {Id})",
            episode.Path,
            range.Start,
            range.End,
            episode.EpisodeId);

        // -vn, -sn, -dn: ignore video, subtitle, and data tracks
        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-vn -sn -dn " +
                "-ss {0} -i \"{1}\" -to {2} -vn -dn -sn -af \"silencedetect=noise={3}dB:duration=0.1\" -f null -",
            range.Start,
            episode.Path,
            range.End - range.Start,
            Plugin.Instance?.Configuration.SilenceDetectionMaximumNoise ?? -50);

        // Cache the output of this command to "GUID-intro-silence-v2"
        var cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-silence-{1}-{2}-v2",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        if (TryLoadSilenceCache(cacheKey, out var cachedRanges))
        {
            return cachedRanges;
        }

        /* Each match will have a type (either "start" or "end") and a timecode (a double).
         *
         * Sample output:
         * [silencedetect @ 0x000000000000] silence_start: 12.34
         * [silencedetect @ 0x000000000000] silence_end: 56.123 | silence_duration: 43.783
        */
        var currentRange = new TimeRange();
        var silenceRanges = new List<TimeRange>();
        var raw = Encoding.UTF8.GetString(GetOutput(args, string.Empty, true));
        foreach (Match match in _silenceDetectionExpression.Matches(raw))
        {
            var isStart = match.Groups["type"].Value == "start";
            var time = Convert.ToDouble(match.Groups["time"].Value, CultureInfo.InvariantCulture);

            if (isStart)
            {
                currentRange.Start = time + range.Start;
            }
            else
            {
                currentRange.End = time + range.Start;
                silenceRanges.Add(new TimeRange(currentRange));
            }
        }

        var result = silenceRanges.ToArray();
        StoreSilenceCache(cacheKey, result);
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
        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-ss {0} -i \"{1}\" -to {2} -an -dn -sn -vf \"blackframe=amount=50:threshold={3}\" -f null -",
            range.Start,
            episode.Path,
            range.End - range.Start,
            threshold);

        // Cache the results to GUID-blackframes-START-END-v1.
        var cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-blackframes-{1}-{2}-v1",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        if (TryLoadBlackFrameCache(cacheKey, out var cachedFrames))
        {
            return FilterBlackFrames(cachedFrames, minimum);
        }

        var raw = Encoding.UTF8.GetString(GetOutput(args, string.Empty, true));
        var frames = ParseBlackFrame(raw);
        StoreBlackFrameCache(cacheKey, frames);

        return FilterBlackFrames(frames, minimum);
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
        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-skip_frame nokey -ss {0} -i \"{1}\" -an -dn -sn -vf \"blackframe=amount=0:threshold={2}\" -f null -",
            episode.CreditsFingerprintStart,
            episode.Path,
            threshold);

        // Cache the results to GUID-blackframes-START-END-v1.
        var cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-blackframes-{1}-alt",
            episode.EpisodeId.ToString("N"),
            episode.CreditsFingerprintStart);

        if (TryLoadBlackFrameCache(cacheKey, out var cachedFrames))
        {
            return cachedFrames;
        }

        var raw = Encoding.UTF8.GetString(GetOutput(args, string.Empty, true));
        var frames = ParseBlackFrame(raw);
        StoreBlackFrameCache(cacheKey, frames);

        return frames;
    }

    private static BlackFrame[] ParseBlackFrame(string raw, int minimum = 0)
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

            var bf = new BlackFrame(percentage, time, frame);

            if (bf.Percentage >= minimum)
            {
                blackFrames.Add(bf);
            }
        }

        return [.. blackFrames];
    }

    private static BlackFrame[] FilterBlackFrames(IReadOnlyList<BlackFrame> frames, int minimum)
    {
        if (minimum <= 0)
        {
            return frames is BlackFrame[] arr ? arr : [.. frames];
        }

        var list = new List<BlackFrame>();
        foreach (var frame in frames)
        {
            if (frame.Percentage >= minimum)
            {
                list.Add(frame);
            }
        }

        return list.ToArray();
    }

    internal static bool TryLoadBlackFrameCache(string cacheKey, out BlackFrame[] frames, string? cacheDirectoryOverride = null)
    {
        frames = [];

        if (!TryGetCacheBasePath(cacheDirectoryOverride, requireCachingEnabled: true, out var basePath))
        {
            return false;
        }

        var path = Path.Join(basePath, cacheKey); // existing caches lack extension
        if (!File.Exists(path))
        {
            path = Path.Join(basePath, cacheKey + BlackFrameCacheExtension);
            if (!File.Exists(path))
            {
                return false;
            }
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            var magic = reader.ReadUInt32();
            if (magic != BlackFrameCacheMagic)
            {
                return false;
            }

            var version = reader.ReadUInt16();
            if (version != BlackFrameCacheVersion)
            {
                Logger?.LogDebug("Unsupported blackframe cache version {Version}", version);
                return false;
            }

            var count = reader.ReadInt32();
            if (count < 0)
            {
                Logger?.LogDebug("Blackframe cache count negative ({Count})", count);
                return false;
            }

            var result = new BlackFrame[count];
            for (var i = 0; i < count; i++)
            {
                var percentage = reader.ReadInt32();
                var time = reader.ReadDouble();
                var frame = reader.ReadInt32();
                result[i] = new BlackFrame(percentage, time, frame);
            }

            frames = result;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            Logger?.LogDebug(ex, "Failed to read blackframe cache {Path}", path);
            return false;
        }
    }

    internal static bool TryLoadSilenceCache(string cacheKey, out TimeRange[] ranges, string? cacheDirectoryOverride = null)
    {
        ranges = [];

        if (!TryGetCacheBasePath(cacheDirectoryOverride, requireCachingEnabled: true, out var basePath))
        {
            return false;
        }

        var path = Path.Join(basePath, cacheKey); // existing caches lack extension
        if (!File.Exists(path))
        {
            path = Path.Join(basePath, cacheKey + SilenceCacheExtension);
            if (!File.Exists(path))
            {
                return false;
            }
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            var magic = reader.ReadUInt32();
            if (magic != SilenceCacheMagic)
            {
                return false;
            }

            var version = reader.ReadUInt16();
            if (version != SilenceCacheVersion)
            {
                Logger?.LogDebug("Unsupported silence cache version {Version}", version);
                return false;
            }

            var count = reader.ReadInt32();
            if (count < 0)
            {
                Logger?.LogDebug("Silence cache count negative ({Count})", count);
                return false;
            }

            var result = new TimeRange[count];
            for (var i = 0; i < count; i++)
            {
                var start = reader.ReadDouble();
                var end = reader.ReadDouble();
                result[i] = new TimeRange(start, end);
            }

            ranges = result;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            Logger?.LogDebug(ex, "Failed to read silence cache {Path}", path);
            return false;
        }
    }

    internal static void StoreBlackFrameCache(string cacheKey, IReadOnlyList<BlackFrame> frames, string? cacheDirectoryOverride = null)
    {
        if (!TryGetCacheBasePath(cacheDirectoryOverride, requireCachingEnabled: true, out var basePath))
        {
            return;
        }

        var path = Path.Join(basePath, cacheKey + BlackFrameCacheExtension);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(BlackFrameCacheMagic);
                writer.Write(BlackFrameCacheVersion);
                writer.Write(frames.Count);

                foreach (var frame in frames)
                {
                    writer.Write(frame.Percentage);
                    writer.Write(frame.Time);
                    writer.Write(frame.Frame);
                }

                writer.Flush();
                stream.Flush(true);
            }

            File.Move(tempPath, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.LogDebug(ex, "Failed to write blackframe cache {Path}", path);
            TryDeleteFile(tempPath);
        }
    }

    internal static void StoreSilenceCache(string cacheKey, IReadOnlyList<TimeRange> ranges, string? cacheDirectoryOverride = null)
    {
        if (!TryGetCacheBasePath(cacheDirectoryOverride, requireCachingEnabled: true, out var basePath))
        {
            return;
        }

        var path = Path.Join(basePath, cacheKey + SilenceCacheExtension);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(SilenceCacheMagic);
                writer.Write(SilenceCacheVersion);
                writer.Write(ranges.Count);

                foreach (var range in ranges)
                {
                    writer.Write(range.Start);
                    writer.Write(range.End);
                }

                writer.Flush();
                stream.Flush(true);
            }

            File.Move(tempPath, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.LogDebug(ex, "Failed to write silence cache {Path}", path);
            TryDeleteFile(tempPath);
        }
    }

    private static bool TryGetCacheBasePath(string? cacheDirectoryOverride, bool requireCachingEnabled, out string basePath)
    {
        if (!string.IsNullOrEmpty(cacheDirectoryOverride))
        {
            basePath = cacheDirectoryOverride;
            return true;
        }

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            basePath = string.Empty;
            return false;
        }

        if (requireCachingEnabled && !plugin.Configuration.CacheFingerprints)
        {
            basePath = string.Empty;
            return false;
        }

        basePath = plugin.FingerprintCachePath;
        return true;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.LogDebug(ex, "Failed to delete cache file {Path}", path);
        }
    }

    /// <summary>
    /// Detects key frames in a media file within a time range.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Time range to search.</param>
    /// <returns>Array of timestamps of key frames.</returns>
    public static double[] DetectKeyFrames(QueuedEpisode episode, TimeRange range)
    {
        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-skip_frame nokey -ss {0} -i \"{1}\" -to {2} -an -dn -sn -vf \"showinfo\" -f null -",
            range.Start,
            episode.Path,
            range.End - range.Start);

        var cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-keyframes-{1}-{2}-v1",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        var keyframes = new List<double>();
        var raw = Encoding.UTF8.GetString(GetOutput(args, cacheKey, stderr: true));

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
                keyframes.Add(timestamp + range.Start);
            }
            else
            {
                Logger?.LogWarning("Failed to parse timestamp: {PtsTimeStr} from line: {Line}", ptsTimeStr, line);
            }
        }

        return [.. keyframes];
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

        // Don't print feature detection logs if the plugin started up okay
        if (ChromaprintLogs["error"] == "okay")
        {
            return logs;
        }

        // Print all remaining logs
        foreach (var kvp in ChromaprintLogs)
        {
            if (kvp.Key == "error" || kvp.Key == "version")
            {
                continue;
            }

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
        Logger?.LogDebug("Checking FFmpeg requirement {Arguments}", arguments);

        var output = Encoding.UTF8.GetString(GetOutput(arguments, string.Empty, false, 2000));
        Logger?.LogTrace("Output of ffmpeg {Arguments}: {Output}", arguments, output);
        ChromaprintLogs[bundleName] = output;

        if (!output.Contains(mustContain, StringComparison.OrdinalIgnoreCase))
        {
            Logger?.LogError("{ErrorMessage}", errorMessage);
            return false;
        }

        Logger?.LogDebug("FFmpeg requirement {Arguments} met", arguments);

        return true;
    }

    /// <summary>
    /// Runs ffmpeg and returns standard output (or error).
    /// If caching is enabled, will use cacheFilename to cache the output of this command.
    /// </summary>
    /// <param name="args">Arguments to pass to ffmpeg.</param>
    /// <param name="cacheFilename">Filename to cache the output of this command to, or string.Empty if this command should not be cached.</param>
    /// <param name="stderr">If standard error should be returned.</param>
    /// <param name="timeout">Timeout (in miliseconds) to wait for ffmpeg to exit.</param>
    private static ReadOnlySpan<byte> GetOutput(
        string args,
        string cacheFilename,
        bool stderr = false,
        int timeout = 60 * 1000)
    {
        var ffmpegPath = Plugin.Instance?.FFmpegPath ?? "ffmpeg";

        // The silencedetect and blackframe filters output data at the info log level.
        var useInfoLevel = args.Contains("silencedetect", StringComparison.OrdinalIgnoreCase) ||
            args.Contains("blackframe", StringComparison.OrdinalIgnoreCase) ||
            args.Contains("showinfo", StringComparison.OrdinalIgnoreCase);

        var logLevel = useInfoLevel ? "info" : "warning";

        var cacheOutput =
            (Plugin.Instance?.Configuration.CacheFingerprints ?? false) &&
            !string.IsNullOrEmpty(cacheFilename);

        // If caching is enabled, try to load the output of this command from the cached file.
        if (cacheOutput)
        {
            // Calculate the absolute path to the cached file.
            cacheFilename = Path.Join(Plugin.Instance!.FingerprintCachePath, cacheFilename);

            // If the cached file exists, return whatever it holds.
            if (File.Exists(cacheFilename))
            {
                Logger?.LogTrace("Returning contents of cache {Cache}", cacheFilename);
                return File.ReadAllBytes(cacheFilename);
            }

            Logger?.LogTrace("Not returning contents of cache {Cache} (not found)", cacheFilename);
        }

        // Prepend some flags to prevent FFmpeg from logging its banner and progress information
        // for each file that is fingerprinted.
        var prependArgument = string.Format(
            CultureInfo.InvariantCulture,
            "-hide_banner -loglevel {0} -threads {1} ",
            logLevel,
            Plugin.Instance?.Configuration.ProcessThreads ?? 0);

        var info = new ProcessStartInfo(ffmpegPath, args.Insert(0, prependArgument))
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            ErrorDialog = false,
            RedirectStandardOutput = !stderr,
            RedirectStandardError = stderr
        };

        using var ffmpeg = new Process { StartInfo = info };
        Logger?.LogDebug("Starting ffmpeg with the following arguments: {Arguments}", ffmpeg.StartInfo.Arguments);

        ffmpeg.Start();

        try
        {
            ffmpeg.PriorityClass = Plugin.Instance?.Configuration.ProcessPriority ?? ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e)
        {
            Logger?.LogDebug("ffmpeg priority could not be modified. {Message}", e.Message);
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

        var output = ms.ToArray();

        // If caching is enabled, cache the output of this command.
        if (cacheOutput)
        {
            File.WriteAllBytes(cacheFilename, output);
        }

        return output;
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
            Logger?.LogTrace("Fingerprint cache hit on {File}", episode.Path);
            return cachedFingerprint;
        }

        Logger?.LogDebug(
            "Fingerprinting [{Start}, {End}] from \"{File}\" (id {Id})",
            start,
            end,
            episode.Path,
            episode.EpisodeId);

        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-ss {0} -i \"{1}\" -to {2} -ac 2 -f chromaprint -fp_format raw -",
            start,
            episode.Path,
            end - start);

        // Returns all fingerprint points as raw 32-bit unsigned integers (little endian).
        var rawPoints = GetOutput(args, string.Empty);
        if (rawPoints.Length == 0 || rawPoints.Length % 4 != 0)
        {
            Logger?.LogWarning("Chromaprint returned {Count} points for \"{Path}\"", rawPoints.Length, episode.Path);
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
    /// This function was created before the unified caching mechanism was introduced (in v0.1.7).
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

        if (!(Plugin.Instance?.Configuration.CacheFingerprints ?? false))
        {
            return false;
        }

        var primaryPath = GetFingerprintCachePath(episode, mode);
        var legacyPath = RemoveFingerprintCacheExtension(primaryPath);

        string? resolvedPath = null;
        if (File.Exists(primaryPath))
        {
            resolvedPath = primaryPath;
        }
        else if (!string.Equals(primaryPath, legacyPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacyPath))
        {
            resolvedPath = legacyPath;
        }
        else
        {
            return false;
        }

        var upgradeFromLegacyPath = !string.Equals(resolvedPath, primaryPath, StringComparison.OrdinalIgnoreCase);
        List<uint>? fingerprintToUpgrade = null;

        try
        {
            using var stream = File.Open(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (TryReadBinaryFingerprint(stream, out var binaryResult))
            {
                fingerprint = binaryResult;
                if (upgradeFromLegacyPath)
                {
                    fingerprintToUpgrade = [.. binaryResult];
                }

                return true;
            }

            stream.Seek(0, SeekOrigin.Begin);

            if (TryReadLegacyFingerprint(stream, out var legacyResult))
            {
                fingerprint = legacyResult;
                fingerprintToUpgrade = [.. legacyResult];

                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.LogError(ex, "Error while reading fingerprint cache from {Path}", resolvedPath);
            return false;
        }
        finally
        {
            if (fingerprintToUpgrade is not null)
            {
                try
                {
                    CacheFingerprint(episode, mode, fingerprintToUpgrade);
                }
                catch (Exception ex)
                {
                    Logger?.LogDebug(ex, "Failed to upgrade legacy fingerprint cache for {Path}", resolvedPath);
                }
            }
        }
    }

    /// <summary>
    /// Cache an episode's fingerprint to disk. If caching is not enabled, calling this function is a no-op.
    /// This function was created before the unified caching mechanism was introduced (in v0.1.7).
    /// </summary>
    /// <param name="episode">Episode to store in cache.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="fingerprint">Fingerprint of the episode to store.</param>
    /// <param name="cacheDirectoryOverride">Optional cache directory to use instead of the plugin default.</param>
    internal static void CacheFingerprint(
        QueuedEpisode episode,
        AnalysisMode mode,
        List<uint> fingerprint,
        string? cacheDirectoryOverride = null)
    {
        // Bail out if caching isn't enabled.
        var usingOverride = !string.IsNullOrEmpty(cacheDirectoryOverride);
        if (!usingOverride && !(Plugin.Instance?.Configuration.CacheFingerprints ?? false))
        {
            return;
        }

        var baseDirectory = cacheDirectoryOverride ?? Plugin.Instance!.FingerprintCachePath;
        var path = GetFingerprintCachePath(episode, mode, baseDirectory);
        var legacyPath = RemoveFingerprintCacheExtension(path);
        var tempPath = path + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(FingerprintCacheMagic);
                writer.Write(FingerprintCacheVersion);
                writer.Write(fingerprint.Count);

                foreach (var point in fingerprint)
                {
                    writer.Write(point);
                }

                writer.Flush();
                stream.Flush(true);
            }

            File.Move(tempPath, path, true);

            if (!string.Equals(path, legacyPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(legacyPath);
                TryDeleteFile(legacyPath + ".tmp");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.LogWarning(ex, "Failed to write fingerprint cache to {Path}", path);
        }
        catch (Exception)
        {
            // Clean up temp file on unexpected errors
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    internal static bool TryReadBinaryFingerprint(Stream stream, out uint[] fingerprint)
    {
        fingerprint = [];

        var startPosition = stream.Position;

        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var magic = reader.ReadUInt32();
            if (magic != FingerprintCacheMagic)
            {
                stream.Seek(startPosition, SeekOrigin.Begin);
                return false;
            }

            var version = reader.ReadUInt16();
            if (version != FingerprintCacheVersion)
            {
                Logger?.LogDebug("Unsupported fingerprint cache version {Version}", version);
                stream.Seek(startPosition, SeekOrigin.Begin);
                return false;
            }

            var count = reader.ReadInt32();
            if (count < 0)
            {
                Logger?.LogDebug("Fingerprint cache reported negative entry count {Count}", count);
                stream.Seek(startPosition, SeekOrigin.Begin);
                return false;
            }

            // Ensure the stream has enough data remaining
            var remainingBytes = stream.Length - stream.Position;
            if (remainingBytes < count * sizeof(uint))
            {
                Logger?.LogDebug("Fingerprint cache truncated: expected {Expected} bytes, got {Actual}", count * sizeof(uint), remainingBytes);
                stream.Seek(startPosition, SeekOrigin.Begin);
                return false;
            }

            var result = new uint[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = reader.ReadUInt32();
            }

            fingerprint = result;
            return true;
        }
        catch (EndOfStreamException)
        {
            Logger?.LogDebug("Encountered unexpected end of fingerprint cache stream");
            stream.Seek(startPosition, SeekOrigin.Begin);
            return false;
        }
        catch (IOException ex)
        {
            Logger?.LogDebug(ex, "I/O error while reading fingerprint cache stream");
            stream.Seek(startPosition, SeekOrigin.Begin);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger?.LogDebug(ex, "Access error while reading fingerprint cache stream");
            stream.Seek(startPosition, SeekOrigin.Begin);
            return false;
        }
        catch (Exception ex)
        {
            Logger?.LogDebug(ex, "Unexpected error while reading fingerprint cache stream");
            stream.Seek(startPosition, SeekOrigin.Begin);
            return false;
        }
    }

    private static bool TryReadLegacyFingerprint(Stream stream, out uint[] fingerprint)
    {
        fingerprint = [];

        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var result = new List<uint>();

            while (reader.ReadLine() is { } line)
            {
                if (uint.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    result.Add(value);
                }
                else
                {
                    Logger?.LogDebug("Invalid legacy fingerprint entry '{Line}'", line);
                    return false;
                }
            }

            fingerprint = [.. result];
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.LogDebug(ex, "Error while reading legacy fingerprint cache");
            return false;
        }
    }

    /// <summary>
    /// Remove a cached episode fingerprint from disk.
    /// </summary>
    /// <param name="id">Episode to remove from cache.</param>
    public static void DeleteEpisodeCache(Guid id)
    {
        var cachePath = Path.Join(
            Plugin.Instance!.FingerprintCachePath,
            id.ToString("N"));

        // File.Delete(cachePath);
        // File.Delete(cachePath + "-intro-silence-v1");
        // File.Delete(cachePath + "-credits");

        var filePattern = Path.GetFileName(cachePath) + "*";
        foreach (var filePath in Directory.EnumerateFiles(Plugin.Instance!.FingerprintCachePath, filePattern))
        {
            Logger?.LogDebug("DeleteEpisodeCache {FilePath}", filePath);
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// Remove cached fingerprints from disk by mode.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    public static void DeleteCacheFiles(AnalysisMode mode)
    {
        foreach (var filePath in Directory.EnumerateFiles(Plugin.Instance!.FingerprintCachePath))
        {
            var shouldDelete = (mode == AnalysisMode.Introduction)
                    ? !filePath.Contains("credit", StringComparison.OrdinalIgnoreCase)
                    && !filePath.Contains("blackframes", StringComparison.OrdinalIgnoreCase)
                    : filePath.Contains("credit", StringComparison.OrdinalIgnoreCase)
                    || filePath.Contains("blackframes", StringComparison.OrdinalIgnoreCase);

            if (shouldDelete)
            {
                File.Delete(filePath);
            }
        }
    }

    /// <summary>
    /// Determines the path an episode should be cached at.
    /// This function was created before the unified caching mechanism was introduced (in v0.1.7).
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>Path.</returns>
    public static string GetFingerprintCachePath(QueuedEpisode episode, AnalysisMode mode)
        => GetFingerprintCachePath(episode, mode, Plugin.Instance!.FingerprintCachePath);

    internal static string GetFingerprintCachePath(QueuedEpisode episode, AnalysisMode mode, string baseDirectory)
    {
        var basePath = BuildFingerprintCacheBasePath(episode, mode, baseDirectory);
        return AppendFingerprintCacheExtension(basePath);
    }

    internal static bool FingerprintCacheExists(QueuedEpisode episode, AnalysisMode mode, string? cacheDirectoryOverride = null)
    {
        if (!TryGetCacheBasePath(cacheDirectoryOverride, requireCachingEnabled: false, out var basePath))
        {
            return false;
        }

        var path = GetFingerprintCachePath(episode, mode, basePath);
        if (File.Exists(path))
        {
            return true;
        }

        var legacyPath = RemoveFingerprintCacheExtension(path);
        return !string.Equals(legacyPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(legacyPath);
    }

    private static string BuildFingerprintCacheBasePath(QueuedEpisode episode, AnalysisMode mode, string baseDirectory)
    {
        var basePath = Path.Join(baseDirectory, episode.EpisodeId.ToString("N"));

        return mode switch
        {
            AnalysisMode.Introduction => basePath,
            AnalysisMode.Credits => basePath + "-credits",
            _ => throw new ArgumentException("Unknown analysis mode " + mode)
        };
    }

    private static string AppendFingerprintCacheExtension(string path)
        => path.EndsWith(FingerprintCacheExtension, StringComparison.OrdinalIgnoreCase)
            ? path
            : path + FingerprintCacheExtension;

    private static string RemoveFingerprintCacheExtension(string path)
        => path.EndsWith(FingerprintCacheExtension, StringComparison.OrdinalIgnoreCase)
            ? path[..^FingerprintCacheExtension.Length]
            : path;

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

    [GeneratedRegex("silence_(?<type>start|end): (?<time>[0-9\\.]+)")]
    private static partial Regex SilenceRegex();

    [GeneratedRegex(@"\[Parsed_blackframe_0 @ [^\]]+\] frame:(\d+) pblack:(\d+) .*? t:([\d.]+)")]
    private static partial Regex BlackFrameRegex();
}
