using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Wrapper for libchromaprint and the silencedetect filter.
/// </summary>
public static class FFmpegWrapper
{
    private static readonly object InvertedIndexCacheLock = new();

    /// <summary>
    /// Used with FFmpeg's silencedetect filter to extract the start and end times of silence.
    /// </summary>
    private static readonly Regex SilenceDetectionExpression = new(
        "silence_(?<type>start|end): (?<time>[0-9\\.]+)");

    /// <summary>
    /// Used with FFmpeg's blackframe filter to extract the time and percentage of black pixels.
    /// </summary>
    private static readonly Regex BlackFrameRegex = new("(pblack|t):[0-9.]+");

    /// <summary>
    /// Gets or sets the logger.
    /// </summary>
    public static ILogger? Logger { get; set; }

    private static Dictionary<string, string> ChromaprintLogs { get; set; } = new();

    private static Dictionary<Guid, Dictionary<uint, int>> InvertedIndexCache { get; set; } = new();

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
        int start, end;

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
            throw new ArgumentException("Unknown analysis mode " + mode.ToString());
        }

        return Fingerprint(episode, mode, start, end);
    }

    /// <summary>
    /// Transforms a Chromaprint into an inverted index of fingerprint points to the last index it appeared at.
    /// </summary>
    /// <param name="id">Episode ID.</param>
    /// <param name="fingerprint">Chromaprint fingerprint.</param>
    /// <returns>Inverted index.</returns>
    public static Dictionary<uint, int> CreateInvertedIndex(Guid id, uint[] fingerprint)
    {
        lock (InvertedIndexCacheLock)
        {
            if (InvertedIndexCache.TryGetValue(id, out var cached))
            {
                return cached;
            }
        }

        var invIndex = new Dictionary<uint, int>();

        for (int i = 0; i < fingerprint.Length; i++)
        {
            // Get the current point.
            var point = fingerprint[i];

            // Append the current sample's timecode to the collection for this point.
            invIndex[point] = i;
        }

        lock (InvertedIndexCacheLock)
        {
            InvertedIndexCache[id] = invIndex;
        }

        return invIndex;
    }

    /// <summary>
    /// Detect ranges of silence in the provided episode.
    /// </summary>
    /// <param name="episode">Queued episode.</param>
    /// <param name="limit">Maximum amount of audio (in seconds) to detect silence in.</param>
    /// <returns>Array of TimeRange objects that are silent in the queued episode.</returns>
    public static TimeRange[] DetectSilence(QueuedEpisode episode, int limit)
    {
        Logger?.LogTrace(
            "Detecting silence in \"{File}\" (limit {Limit}, id {Id})",
            episode.Path,
            limit,
            episode.EpisodeId);

        // -vn, -sn, -dn: ignore video, subtitle, and data tracks
        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-vn -sn -dn " +
                "-i \"{0}\" -to {1} -af \"silencedetect=noise={2}dB:duration=0.1\" -f null -",
            episode.Path,
            limit,
            Plugin.Instance?.Configuration.SilenceDetectionMaximumNoise ?? -50);

        // Cache the output of this command to "GUID-intro-silence-v1"
        var cacheKey = episode.EpisodeId.ToString("N") + "-intro-silence-v1";

        var currentRange = new TimeRange();
        var silenceRanges = new List<TimeRange>();

        /* Each match will have a type (either "start" or "end") and a timecode (a double).
         *
         * Sample output:
         * [silencedetect @ 0x000000000000] silence_start: 12.34
         * [silencedetect @ 0x000000000000] silence_end: 56.123 | silence_duration: 43.783
        */
        var raw = Encoding.UTF8.GetString(GetOutput(args, cacheKey, true));
        foreach (Match match in SilenceDetectionExpression.Matches(raw))
        {
            var isStart = match.Groups["type"].Value == "start";
            var time = Convert.ToDouble(match.Groups["time"].Value, CultureInfo.InvariantCulture);

            if (isStart)
            {
                currentRange.Start = time;
            }
            else
            {
                currentRange.End = time;
                silenceRanges.Add(new TimeRange(currentRange));
            }
        }

        return silenceRanges.ToArray();
    }

    /// <summary>
    /// Finds the location of all black frames in a media file within a time range.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="range">Time range to search.</param>
    /// <param name="minimum">Percentage of the frame that must be black.</param>
    /// <returns>Array of frames that are mostly black.</returns>
    public static BlackFrame[] DetectBlackFrames(
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
        var raw = Encoding.UTF8.GetString(GetOutput(args, cacheKey, true));
        foreach (var line in raw.Split('\n'))
        {
            var matches = BlackFrameRegex.Matches(line);
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

        return blackFrames.ToArray();
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
    /// <param name="mustContain">String that the output must contain. Case insensitive.</param>
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
            args.Contains("blackframe", StringComparison.OrdinalIgnoreCase);

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

        // Prepend some flags to prevent FFmpeg from logging it's banner and progress information
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

        var ffmpeg = new Process
        {
            StartInfo = info
        };

        Logger?.LogDebug(
            "Starting ffmpeg with the following arguments: {Arguments}",
            ffmpeg.StartInfo.Arguments);

        ffmpeg.Start();

        try
        {
            ffmpeg.PriorityClass = Plugin.Instance?.Configuration.ProcessPriority ?? ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e)
        {
            Logger?.LogDebug(
                "ffmpeg priority could not be modified. {Message}",
                e.Message);
        }

        using (MemoryStream ms = new MemoryStream())
        {
            var buf = new byte[4096];
            var bytesRead = 0;

            do
            {
                var streamReader = stderr ? ffmpeg.StandardError : ffmpeg.StandardOutput;
                bytesRead = streamReader.BaseStream.Read(buf, 0, buf.Length);
                ms.Write(buf, 0, bytesRead);
            }
            while (bytesRead > 0);

            ffmpeg.WaitForExit(timeout);

            var output = ms.ToArray();

            // If caching is enabled, cache the output of this command.
            if (cacheOutput)
            {
                File.WriteAllBytes(cacheFilename, output);
            }

            return output;
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
    private static uint[] Fingerprint(QueuedEpisode episode, AnalysisMode mode, int start, int end)
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

        // Returns all fingerprint points as raw 32 bit unsigned integers (little endian).
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

        return results.ToArray();
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
        fingerprint = Array.Empty<uint>();

        // If fingerprint caching isn't enabled, don't try to load anything.
        if (!(Plugin.Instance?.Configuration.CacheFingerprints ?? false))
        {
            return false;
        }

        var path = GetFingerprintCachePath(episode, mode);

        // If this episode isn't cached, bail out.
        if (!File.Exists(path))
        {
            return false;
        }

        var raw = File.ReadAllLines(path, Encoding.UTF8);
        var result = new List<uint>();

        // Read each stringified uint.
        result.EnsureCapacity(raw.Length);

        try
        {
            foreach (var rawNumber in raw)
            {
                result.Add(Convert.ToUInt32(rawNumber, CultureInfo.InvariantCulture));
            }
        }
        catch (FormatException)
        {
            // Occurs when the cached fingerprint is corrupt.
            Logger?.LogDebug(
                "Cached fingerprint for {Path} ({Id}) is corrupt, ignoring cache",
                episode.Path,
                episode.EpisodeId);

            return false;
        }

        fingerprint = result.ToArray();
        return true;
    }

    /// <summary>
    /// Cache an episode's fingerprint to disk. If caching is not enabled, calling this function is a no-op.
    /// This function was created before the unified caching mechanism was introduced (in v0.1.7).
    /// </summary>
    /// <param name="episode">Episode to store in cache.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="fingerprint">Fingerprint of the episode to store.</param>
    private static void CacheFingerprint(
        QueuedEpisode episode,
        AnalysisMode mode,
        List<uint> fingerprint)
    {
        // Bail out if caching isn't enabled.
        if (!(Plugin.Instance?.Configuration.CacheFingerprints ?? false))
        {
            return;
        }

        // Stringify each data point.
        var lines = new List<string>();
        foreach (var number in fingerprint)
        {
            lines.Add(number.ToString(CultureInfo.InvariantCulture));
        }

        // Cache the episode.
        File.WriteAllLinesAsync(
            GetFingerprintCachePath(episode, mode),
            lines,
            Encoding.UTF8).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines the path an episode should be cached at.
    /// This function was created before the unified caching mechanism was introduced (in v0.1.7).
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="mode">Analysis mode.</param>
    private static string GetFingerprintCachePath(QueuedEpisode episode, AnalysisMode mode)
    {
        var basePath = Path.Join(
            Plugin.Instance!.FingerprintCachePath,
            episode.EpisodeId.ToString("N"));

        if (mode == AnalysisMode.Introduction)
        {
            return basePath;
        }
        else if (mode == AnalysisMode.Credits)
        {
            return basePath + "-credits";
        }
        else
        {
            throw new ArgumentException("Unknown analysis mode " + mode.ToString());
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
}
