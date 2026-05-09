// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Checks FFmpeg installation capabilities and provides diagnostic logs.
/// </summary>
public sealed partial class FFmpegCapabilityService : IFFmpegCapabilityService
{
    private readonly IFFmpegRunner _runner;
    private readonly ILogger<FFmpegCapabilityService> _logger;
    private readonly ConcurrentDictionary<string, string> _chromaprintLogs = new();
    private volatile bool _ffmpegCheckPassed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegCapabilityService"/> class.
    /// </summary>
    /// <param name="runner">FFmpeg process runner.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runner"/> or <paramref name="logger"/> is <see langword="null"/>.</exception>
    public FFmpegCapabilityService(
        IFFmpegRunner runner,
        ILogger<FFmpegCapabilityService> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CheckFFmpegVersion()
    {
        // Only cache successful results. Failures are retried so that installing or
        // upgrading FFmpeg takes effect without restarting the server.
        if (_ffmpegCheckPassed)
        {
            return true;
        }

        try
        {
            // Always log ffmpeg's version information.
            if (!CheckFFmpegRequirement(
                "-version",
                "ffmpeg",
                "version",
                "Unknown error with FFmpeg version"))
            {
                _chromaprintLogs["error"] = "unknown_error";
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
                _chromaprintLogs["error"] = "chromaprint_not_supported";
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
                _chromaprintLogs["error"] = "fp_format_not_supported";
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
                _chromaprintLogs["error"] = "silencedetect_not_supported";
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                return false;
            }

            LogFfmpegVersionValid(_logger);

            _chromaprintLogs["error"] = "okay";
            _ffmpegCheckPassed = true;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _chromaprintLogs["error"] = "unknown_error";
            WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
            return false;
        }
    }

    /// <inheritdoc />
    public string GetChromaprintLogs()
    {
        // Print the FFmpeg detection status at the top.
        // Format: "* FFmpeg: `error`"
        // Append two newlines to separate the bulleted list from the logs
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"* FFmpeg: `{_chromaprintLogs.GetValueOrDefault("error", "not_checked")}`\n\n");

        // Always include ffmpeg version information first, then known feature detection logs
        // in a stable order. Any future diagnostic keys are appended deterministically.
        AppendFFmpegLog(sb, "version");
        AppendFFmpegLog(sb, "muxer list");
        AppendFFmpegLog(sb, "chromaprint options");
        AppendFFmpegLog(sb, "silencedetect options");

        foreach (var key in _chromaprintLogs.Keys
            .Where(static key => key is not "error" and not "version" and not "muxer list" and not "chromaprint options" and not "silencedetect options")
            .Order(StringComparer.Ordinal))
        {
            AppendFFmpegLog(sb, key);
        }

        return sb.ToString();
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
    private bool CheckFFmpegRequirement(
        string arguments,
        string mustContain,
        string bundleName,
        string errorMessage)
    {
        LogCheckingRequirement(_logger, arguments);

        var output = Encoding.UTF8.GetString(
            _runner.Run(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), false, 2000).Output);

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

    private void AppendFFmpegLog(StringBuilder sb, string key)
    {
        /* Format:
        * FFmpeg NAME:
        * ```
        * LOGS
        * ```
        */

        if (!_chromaprintLogs.TryGetValue(key, out var logValue))
        {
            return;
        }

        sb.Append(CultureInfo.InvariantCulture, $"FFmpeg {key}:\n```\n");
        sb.Append(logValue);

        // Ensure the closing triple backtick is on a separate line
        if (logValue.Length == 0 || logValue[^1] != '\n')
        {
            sb.Append('\n');
        }

        sb.Append("```\n\n");
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Installed version of ffmpeg meets fingerprinting requirements")]
    private static partial void LogFfmpegVersionValid(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Checking FFmpeg requirement {Arguments}")]
    private static partial void LogCheckingRequirement(ILogger logger, string arguments);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Output of ffmpeg {Arguments}: {Output}")]
    private static partial void LogFfmpegOutput(ILogger logger, string arguments, string output);

    [LoggerMessage(Level = LogLevel.Error, Message = "{ErrorMessage}")]
    private static partial void LogFfmpegRequirementFailed(ILogger logger, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FFmpeg requirement {Arguments} met")]
    private static partial void LogFfmpegRequirementMet(ILogger logger, string arguments);
}
