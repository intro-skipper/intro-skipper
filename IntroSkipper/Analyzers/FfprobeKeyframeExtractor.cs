// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Extracts keyframe data using ffprobe for universal format support.
/// FFprobe outputs timestamps in seconds (pts_time field), so no conversion is needed.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FfprobeKeyframeExtractor"/> class.
/// </remarks>
/// <param name="ffProbePath">Path to ffprobe executable.</param>
/// <param name="logger">Logger instance.</param>
public class FfprobeKeyframeExtractor(string ffProbePath, ILogger logger) : IKeyframeExtractor
{
    private readonly string _ffProbePath = ffProbePath;
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Extracts keyframe data from a video file using ffprobe.
    /// </summary>
    /// <param name="filePath">Path to the video file.</param>
    /// <returns>KeyframeData with duration and keyframes in seconds.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file or ffprobe executable does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when ffprobe execution fails or returns invalid data.</exception>
    public KeyframeData GetKeyframeData(string filePath)
    {
        if (!File.Exists(_ffProbePath))
        {
            throw new FileNotFoundException($"ffprobe executable not found: {_ffProbePath}", _ffProbePath);
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Video file not found: {filePath}", filePath);
        }

        try
        {
            var duration = ExtractDuration(filePath);
            var keyframes = ExtractKeyframes(filePath);
            return new KeyframeData(duration, keyframes);
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to extract keyframe data from file: {filePath}. Error: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Extracts duration from a video file using ffprobe.
    /// Prioritizes stream duration over format duration when both are available.
    /// </summary>
    /// <param name="filePath">Path to the video file.</param>
    /// <returns>Duration in seconds.</returns>
    private double ExtractDuration(string filePath)
    {
        // Try stream duration first (prioritized per requirement 2.3)
        try
        {
            var streamDuration = ExecuteFfprobe(
                $"-v error -show_entries stream=duration -select_streams v:0 -of csv=p=0 \"{filePath}\"");

            if (!string.IsNullOrWhiteSpace(streamDuration) &&
                double.TryParse(streamDuration.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var streamDurationValue))
            {
                _logger.LogDebug("Extracted stream duration: {Duration}s from {File}", streamDurationValue, filePath);
                return streamDurationValue;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to extract stream duration, trying format duration");
        }

        // Fall back to format duration
        try
        {
            var formatDuration = ExecuteFfprobe(
                $"-v error -show_entries format=duration -of csv=p=0 \"{filePath}\"");

            if (!string.IsNullOrWhiteSpace(formatDuration) &&
                double.TryParse(formatDuration.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var formatDurationValue))
            {
                _logger.LogDebug("Extracted format duration: {Duration}s from {File}", formatDurationValue, filePath);
                return formatDurationValue;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to extract duration from file: {filePath}",
                ex);
        }

        throw new InvalidOperationException($"Could not extract duration from file: {filePath}");
    }

    /// <summary>
    /// Executes ffprobe with the given arguments and returns stdout.
    /// </summary>
    /// <param name="arguments">Arguments to pass to ffprobe.</param>
    /// <returns>Standard output from ffprobe.</returns>
    private string ExecuteFfprobe(string arguments)
    {
        var processInfo = new ProcessStartInfo(_ffProbePath, arguments)
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = processInfo };
        try
        {
            process.Start();

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not set process priority to BelowNormal");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(30000)) // 30 second timeout
            {
                process.Kill();
                throw new InvalidOperationException("ffprobe process timed out after 30 seconds");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffprobe exited with code {process.ExitCode}. Error: {error}");
            }

            return output;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to start ffprobe process. Arguments: {arguments}",
                ex);
        }
    }

    /// <summary>
    /// Extracts keyframe timestamps from a video file using ffprobe.
    /// </summary>
    /// <param name="filePath">Path to the video file.</param>
    /// <returns>List of keyframe timestamps in seconds.</returns>
    private List<double> ExtractKeyframes(string filePath)
    {
        Process? process = null;
        try
        {
            var arguments = $"-v error -show_packets -select_streams v:0 -show_entries packet=pts_time,flags -of csv=p=0 \"{filePath}\"";

            var processInfo = new ProcessStartInfo(_ffProbePath, arguments)
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process = new Process { StartInfo = processInfo };
            process.Start();

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not set process priority to BelowNormal");
            }

            // Parse the CSV output stream
            var keyframes = ParseStream(process.StandardOutput.BaseStream);

            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(30000)) // 30 second timeout
            {
                process.Kill();
                throw new InvalidOperationException("ffprobe process timed out after 30 seconds");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffprobe exited with code {process.ExitCode}. Error: {error}");
            }

            _logger.LogDebug("Extracted {Count} keyframes from {File}", keyframes.Count, filePath);

            return keyframes;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to extract keyframes from file: {filePath}",
                ex);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Parses ffprobe CSV output to extract keyframe timestamps.
    /// FFprobe already outputs timestamps in seconds (pts_time field).
    /// </summary>
    /// <param name="stream">Stream containing CSV data.</param>
    /// <returns>List of keyframe timestamps in seconds.</returns>
    private List<double> ParseStream(Stream stream)
    {
        var keyframes = new List<double>();

        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Skip empty or whitespace-only lines
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Split by comma delimiter
            var parts = line.Split(',');

            // Skip malformed lines (must have at least 2 fields: pts_time and flags)
            if (parts.Length < 2)
            {
                _logger.LogWarning("Skipping malformed CSV line (< 2 fields): {Line}", line);
                continue;
            }

            var ptsTime = parts[0].Trim();
            var flags = parts[1].Trim();

            // Check if this packet is a keyframe (flags contains "K_")
            if (flags.Contains("K_", StringComparison.Ordinal))
            {
                // Parse pts_time as double (already in seconds, no conversion needed)
                if (double.TryParse(ptsTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var timestamp))
                {
                    keyframes.Add(timestamp);
                }
                else
                {
                    _logger.LogWarning("Failed to parse timestamp: {PtsTime} from line: {Line}", ptsTime, line);
                }
            }
        }

        // Sort keyframes in ascending order
        keyframes.Sort();

        return keyframes;
    }
}
