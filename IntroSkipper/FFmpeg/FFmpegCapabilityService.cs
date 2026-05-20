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
    private static readonly TimeSpan CapabilityCheckTimeout = TimeSpan.FromSeconds(2);

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

    /// <summary>
    /// Check that the installed version of ffmpeg supports chromaprint.
    /// A successful result is cached for the lifetime of the service instance;
    /// failures are retried on every call so that installing or upgrading FFmpeg
    /// takes effect without restarting the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true if a compatible version of ffmpeg is installed, false on any error.</returns>
    public async Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
    {
        // Only cache successful results. Failures are retried so that installing or
        // upgrading FFmpeg takes effect without restarting the server.
        if (_ffmpegCheckPassed)
        {
            return true;
        }

        try
        {
            if (!await CheckFFmpegRequirementAsync(
                    "-version",
                    "ffmpeg",
                    "version",
                    "Unknown error with FFmpeg version",
                    "unknown_error",
                    cancellationToken).ConfigureAwait(false) ||
                !await CheckFFmpegRequirementAsync(
                    "-muxers",
                    "chromaprint",
                    "muxer list",
                    "The installed version of ffmpeg does not support chromaprint",
                    "chromaprint_not_supported",
                    cancellationToken).ConfigureAwait(false) ||
                !await CheckFFmpegRequirementAsync(
                    "-h muxer=chromaprint",
                    "binary raw fingerprint",
                    "chromaprint options",
                    "The installed version of ffmpeg does not support raw binary fingerprints",
                    "fp_format_not_supported",
                    cancellationToken).ConfigureAwait(false) ||
                !await CheckFFmpegRequirementAsync(
                    "-h filter=silencedetect",
                    "noise tolerance",
                    "silencedetect options",
                    "The installed version of ffmpeg does not support the silencedetect filter",
                    "silencedetect_not_supported",
                    cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            LogFfmpegVersionValid(_logger);

            _chromaprintLogs["error"] = "okay";
            WarningManager.ClearFlag(PluginWarning.IncompatibleFFmpegBuild);
            _ffmpegCheckPassed = true;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _chromaprintLogs["error"] = "unknown_error";
            WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
            return false;
        }
    }

    /// <summary>
    /// Gets Chromaprint debugging logs.
    /// </summary>
    /// <returns>Markdown formatted logs.</returns>
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
    /// the provided string. On failure, sets the error key and incompatible-build warning.
    /// </summary>
    /// <param name="arguments">Arguments to pass to FFmpeg.</param>
    /// <param name="mustContain">String that the output must contain. Case-insensitive.</param>
    /// <param name="bundleName">Support bundle key to store FFmpeg's output under.</param>
    /// <param name="errorMessage">Error message to log if this requirement is not met.</param>
    /// <param name="errorKey">Value to store in <c>_chromaprintLogs["error"]</c> on failure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true on success, false on error.</returns>
    private async Task<bool> CheckFFmpegRequirementAsync(
        string arguments,
        string mustContain,
        string bundleName,
        string errorMessage,
        string errorKey,
        CancellationToken cancellationToken)
    {
        LogCheckingRequirement(_logger, arguments);

        var result = await _runner.RunAsync(
            arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            FFmpegOutputStream.Stdout,
            CapabilityCheckTimeout,
            cancellationToken).ConfigureAwait(false);
        var output = Encoding.UTF8.GetString(result.Output);

        LogFfmpegOutput(_logger, arguments, output);

        _chromaprintLogs[bundleName] = output;

        if (result.Status != FFmpegProcessStatus.Completed || result.ExitCode != 0 || !output.Contains(mustContain, StringComparison.OrdinalIgnoreCase))
        {
            LogFfmpegRequirementFailed(_logger, errorMessage);
            _chromaprintLogs["error"] = errorKey;
            WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
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
