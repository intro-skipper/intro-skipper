// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Helper;
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

    /// <summary>
    /// Check that the installed version of ffmpeg supports chromaprint.
    /// </summary>
    /// <returns>true if a compatible version of ffmpeg is installed, false on any error.</returns>
    public static async Task<bool> CheckFFmpegVersion()
    {
        try
        {
            // Always log ffmpeg's version information.
            if (!await CheckFFmpegRequirementAsync(
                "-version",
                "ffmpeg",
                "version",
                "Unknown error with FFmpeg version").ConfigureAwait(false))
            {
                ChromaprintLogs["error"] = "unknown_error";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            // First, validate that the installed version of ffmpeg supports chromaprint at all.
            if (!await CheckFFmpegRequirementAsync(
                "-muxers",
                "chromaprint",
                "muxer list",
                "The installed version of ffmpeg does not support chromaprint").ConfigureAwait(false))
            {
                ChromaprintLogs["error"] = "chromaprint_not_supported";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            // Second, validate that the Chromaprint muxer understands the "-fp_format raw" option.
            if (!await CheckFFmpegRequirementAsync(
                "-h muxer=chromaprint",
                "binary raw fingerprint",
                "chromaprint options",
                "The installed version of ffmpeg does not support raw binary fingerprints").ConfigureAwait(false))
            {
                ChromaprintLogs["error"] = "fp_format_not_supported";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            // Third, validate that ffmpeg supports of the all required silencedetect options.
            if (!await CheckFFmpegRequirementAsync(
                "-h filter=silencedetect",
                "noise tolerance",
                "silencedetect options",
                "The installed version of ffmpeg does not support the silencedetect filter").ConfigureAwait(false))
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
    public static async Task<uint[]> Fingerprint(QueuedEpisode episode, AnalysisMode mode)
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

        return await Fingerprint(episode, mode, start, end).ConfigureAwait(false);
    }

    /// <summary>
    /// Detect ranges of silence in the provided episode.
    /// </summary>
    /// <param name="episode">Queued episode.</param>
    /// <param name="range">Time range to search.</param>
    /// <returns>Array of TimeRange objects that are silent in the queued episode.</returns>
    public static async Task<TimeRange[]> DetectSilence(QueuedEpisode episode, TimeRange range)
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
                "-ss {0} -i \"{1}\" -to {2} -af \"silencedetect=noise={3}dB:duration=0.1\" -f null -",
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

        var currentRange = new TimeRange();
        var silenceRanges = new List<TimeRange>();

        /* Each match will have a type (either "start" or "end") and a timecode (a double).
         *
         * Sample output:
         * [silencedetect @ 0x000000000000] silence_start: 12.34
         * [silencedetect @ 0x000000000000] silence_end: 56.123 | silence_duration: 43.783
        */
        var raw = Encoding.UTF8.GetString((await GetOutputAsync(args, cacheKey, true).ConfigureAwait(false)).Span);
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

        return [.. silenceRanges];
    }

    /// <summary>
    /// Finds the location of all black frames in a media file within a time range.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="minimum">Percentage of the frame that must be black.</param>
    /// <returns>Array of frames that are mostly black.</returns>
    public static async Task<BlackFrame[]> DetectBlackFrames(
        QueuedEpisode episode,
        TimeRange range,
        int minimum)
    {
        // Seek to the start of the time range and find frames that are at least 50% black.
        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-ss {0} -i \"{1}\" -to {2} -an -dn -sn -vf \"blackframe=amount=50\" -f null -",
            range.Start,
            episode.Path,
            range.End - range.Start);

        // Cache the results to GUID-blackframes-START-END-v1.
        var cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-blackframes-{1}-{2}-v1",
            episode.EpisodeId.ToString("N"),
            range.Start,
            range.End);

        var blackFrames = new List<BlackFrame>();

        /* Run the blackframe filter.
         *
         * Sample output:
         * [Parsed_blackframe_0 @ 0x0000000] frame:1 pblack:99 pts:43 t:0.043000 type:B last_keyframe:0
         * [Parsed_blackframe_0 @ 0x0000000] frame:2 pblack:99 pts:85 t:0.085000 type:B last_keyframe:0
         */
        var raw = Encoding.UTF8.GetString((await GetOutputAsync(args, cacheKey, true).ConfigureAwait(false)).Span);
        foreach (var line in raw.Split('\n'))
        {
            // There is no FFmpeg flag to hide metadata such as description
            // In our case, the metadata contained something that matched the regex.
            if (line.StartsWith("[Parsed_blackframe_", StringComparison.OrdinalIgnoreCase))
            {
                var matches = _blackFrameRegex.Matches(line);
                if (matches.Count != 2)
                {
                    continue;
                }

                var (strPercent, strTime) = (
                    matches[0].Value.Split(':')[1],
                    matches[1].Value.Split(':')[1]
                );

                var bf = new BlackFrame(
                    Convert.ToInt32(strPercent, CultureInfo.InvariantCulture),
                    Convert.ToDouble(strTime, CultureInfo.InvariantCulture));

                if (bf.Percentage > minimum)
                {
                    blackFrames.Add(bf);
                }
            }
        }

        return [.. blackFrames];
    }

    /// <summary>
    /// Detects key frames in a media file within a time range.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Time range to search.</param>
    /// <returns>Array of timestamps of key frames.</returns>
    public static async Task<double[]> DetectKeyFrames(QueuedEpisode episode, TimeRange range)
    {
        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-ss {0} -i \"{1}\" -to {2} -an -dn -sn -vf \"select='eq(pict_type,I)',showinfo\" -f null -",
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
        var raw = Encoding.UTF8.GetString((await GetOutputAsync(args, cacheKey, stderr: true).ConfigureAwait(false)).Span);

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("type:I", StringComparison.OrdinalIgnoreCase) ||
                !line.Contains("iskey:1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
    private static async Task<bool> CheckFFmpegRequirementAsync(
        string arguments,
        string mustContain,
        string bundleName,
        string errorMessage)
    {
        Logger?.LogDebug("Checking FFmpeg requirement {Arguments}", arguments);

        var output = Encoding.UTF8.GetString((await GetOutputAsync(arguments, string.Empty, false, 2000).ConfigureAwait(false)).Span);
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
    private static async Task<ReadOnlyMemory<byte>> GetOutputAsync(
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

            try
            {
                // If the cached file exists, return whatever it holds.
                if (File.Exists(cacheFilename))
                {
                    Logger?.LogTrace("Returning contents of cache {Cache}", cacheFilename);
                    return await File.ReadAllBytesAsync(cacheFilename).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger?.LogWarning(ex, "Failed to read cache file {Cache}", cacheFilename);
            }

            Logger?.LogTrace("Not returning contents of cache {Cache} (not found)", cacheFilename);
        }

        // Prepend some flags to prevent FFmpeg from logging it's banner and progress information
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

        try
        {
            if (!ffmpeg.Start())
            {
                throw new InvalidOperationException("Failed to start FFmpeg process");
            }

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
            int bytesRead;

            using (var streamReader = stderr ? ffmpeg.StandardError : ffmpeg.StandardOutput)
            {
                while ((bytesRead = await streamReader.BaseStream.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    await ms.WriteAsync(buf.AsMemory(0, bytesRead)).ConfigureAwait(false);
                }
            }

            try
            {
                await Task.WhenAny(
                    Task.Run(ffmpeg.WaitForExit),
                    Task.Delay(timeout)).ConfigureAwait(false);

                if (!ffmpeg.HasExited)
                {
                    try
                    {
                        ffmpeg.Kill();
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning(ex, "Failed to kill FFmpeg process after timeout");
                    }

                    throw new TimeoutException($"FFmpeg process did not complete within {timeout}ms timeout");
                }

                if (ffmpeg.ExitCode != 0)
                {
                    throw new InvalidOperationException($"FFmpeg process exited with code {ffmpeg.ExitCode}");
                }
            }
            catch (Exception) when (ffmpeg.HasExited)
            {
                // Process already exited, ignore any exceptions from killing it
            }

            var output = ms.ToArray();

            // If caching is enabled, cache the output of this command.
            if (cacheOutput)
            {
                try
                {
                    await File.WriteAllBytesAsync(cacheFilename, output).ConfigureAwait(false);
                    Logger?.LogTrace("Successfully cached output to {Cache}", cacheFilename);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger?.LogWarning(ex, "Failed to write cache file {Cache}", cacheFilename);
                }
            }

            return output;
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error executing FFmpeg command: {Arguments}", info.Arguments);
            throw;
        }
    }

    /// <summary>
    /// Fingerprint a queued episode.
    /// </summary>
    /// <param name="episode">Queued episode to fingerprint.</param>
    /// <param name="mode">Portion of media file to fingerprint.</param>
    /// <param name="start">Time (in seconds) relative to the start of the file to start fingerprinting from.</param>
    /// <param name="end">Time (in seconds) relative to the start of the file to stop fingerprinting at.</param>
    /// <returns>Numerical fingerprint points.</returns>
    private static async Task<uint[]> Fingerprint(QueuedEpisode episode, AnalysisMode mode, double start, double end)
    {
        // Try to load this episode from cache before running ffmpeg.
        if ((await LoadCachedFingerprintAsync(episode, mode).ConfigureAwait(false)).TryOut(out uint[] cachedFingerprint))
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

        // Returns all fingerprint points as raw 32 bit unsigned integers (little endian).
        var rawPoints = await GetOutputAsync(args, string.Empty).ConfigureAwait(false);
        if (rawPoints.Length == 0 || rawPoints.Length % 4 != 0)
        {
            Logger?.LogWarning("Chromaprint returned {Count} points for \"{Path}\"", rawPoints.Length, episode.Path);
            throw new FingerprintException("chromaprint output for \"" + episode.Path + "\" was malformed");
        }

        var results = new List<uint>();
        for (var i = 0; i < rawPoints.Length; i += 4)
        {
            var rawPoint = rawPoints.Slice(i, 4).ToArray();
            results.Add(BitConverter.ToUInt32(rawPoint));
        }

        // Try to cache this fingerprint.
        await CacheFingerprintAsync(episode, mode, results).ConfigureAwait(false);

        return [.. results];
    }

    /// <summary>
    /// Tries to load an episode's fingerprint from cache. If caching is not enabled, calling this function is a no-op.
    /// This function was created before the unified caching mechanism was introduced (in v0.1.7).
    /// </summary>
    /// <param name="episode">Episode to try to load from cache.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>true if the episode was successfully loaded from cache, false on any other error.</returns>
    private static async Task<(bool Success, uint[] Fingerprint)> LoadCachedFingerprintAsync(
        QueuedEpisode episode,
        AnalysisMode mode)
    {
        // If fingerprint caching isn't enabled, don't try to load anything.
        if (!(Plugin.Instance?.Configuration.CacheFingerprints ?? false))
        {
            return (false, []);
        }

        var path = GetFingerprintCachePath(episode, mode);

        // If this episode isn't cached, bail out.
        if (!File.Exists(path))
        {
            return (false, []);
        }

        string[] raw;
        try
        {
            raw = await File.ReadAllLinesAsync(path, Encoding.UTF8).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Logger?.LogError(ex, "I/O error while reading fingerprint cache from {Path}", path);
            return (false, []);
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger?.LogError(ex, "Access error while reading fingerprint cache from {Path}", path);
            return (false, []);
        }

        var result = new List<uint>(raw.Length);

        foreach (var rawNumber in raw)
        {
            if (uint.TryParse(rawNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint number))
            {
                result.Add(number);
            }
            else
            {
                Logger?.LogDebug(
                    "Invalid fingerprint entry '{RawNumber}' found in cache for {Path} ({Id}), ignoring cache",
                    rawNumber,
                    episode.Path,
                    episode.EpisodeId);
                return (false, []);
            }
        }

        return (true, [.. result]);
    }

    /// <summary>
    /// Cache an episode's fingerprint to disk. If caching is not enabled, calling this function is a no-op.
    /// This function was created before the unified caching mechanism was introduced (in v0.1.7).
    /// </summary>
    /// <param name="episode">Episode to store in cache.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="fingerprint">Fingerprint of the episode to store.</param>
    private static async Task CacheFingerprintAsync(
        QueuedEpisode episode,
        AnalysisMode mode,
        List<uint> fingerprint)
    {
        // Bail out if caching isn't enabled.
        if (!(Plugin.Instance?.Configuration.CacheFingerprints ?? false))
        {
            return;
        }

        var path = GetFingerprintCachePath(episode, mode);

        try
        {
            // Use StringBuilder for more efficient string concatenation
            var sb = new StringBuilder(fingerprint.Count * 11); // Estimate ~11 chars per number
            foreach (var number in fingerprint)
            {
                sb.AppendLine(number.ToString(CultureInfo.InvariantCulture));
            }

            await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);

            Logger?.LogTrace("Successfully cached fingerprint for {Path}", episode.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.LogError(ex, "Failed to cache fingerprint for {Path} to {CachePath}", episode.Path, path);
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
    {
        var basePath = Path.Join(
            Plugin.Instance!.FingerprintCachePath,
            episode.EpisodeId.ToString("N"));

        if (mode == AnalysisMode.Introduction)
        {
            return basePath;
        }

        if (mode == AnalysisMode.Credits)
        {
            return basePath + "-credits";
        }

        throw new ArgumentException("Unknown analysis mode " + mode);
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

    [GeneratedRegex("silence_(?<type>start|end): (?<time>[0-9\\.]+)")]
    private static partial Regex SilenceRegex();

    [GeneratedRegex("(pblack|t):[0-9.]+")]
    private static partial Regex BlackFrameRegex();
}
