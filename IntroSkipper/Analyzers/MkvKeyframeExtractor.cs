// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.MediaEncoding.Keyframes;
using NEbml.Core;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Extracts keyframe data from MKV files by parsing EBML structure.
/// MKV files store timestamps in units of TimestampScale nanoseconds, which are converted to .NET ticks.
/// </summary>
/// <remarks>
/// This implementation directly parses the MKV container format to extract keyframe positions.
/// Timestamps are converted from MKV's TimestampScale units to .NET ticks (1 tick = 100 nanoseconds).
/// </remarks>
public class MkvKeyframeExtractor : IKeyframeExtractor
{
    /// <summary>
    /// Extracts keyframe data from an MKV file.
    /// </summary>
    /// <param name="filePath">Path to the MKV file.</param>
    /// <returns>KeyframeData with duration and keyframes in ticks (1 tick = 100 nanoseconds).</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist or cannot be accessed.</exception>
    /// <exception cref="InvalidDataException">Thrown when the EBML structure is invalid or corrupted.</exception>
    public KeyframeData GetKeyframeData(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"MKV file not found: {filePath}", filePath);
        }

        try
        {
            // Use buffered stream
            using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536); // 64KB buffer
            using var reader = new EbmlReader(fileStream);

            // Default values
            long timestampScale = 1_000_000; // Default: 1ms per unit (1,000,000 nanoseconds)
            long durationTicks = 0L;

            // Pre-allocate list with typical capacity
            var keyframes = new List<long>(capacity: 2000);
            bool foundInfo = false;
            bool foundCues = false;

            // Read EBML header
            if (!reader.ReadNext())
            {
                throw new InvalidDataException($"Failed to read EBML header from: {filePath}");
            }

            // Parse the document to find Info and Cues sections
            while (reader.ReadNext())
            {
                var elementId = reader.ElementId.EncodedValue;

                // Info section (0x1549A966) - contains TimestampScale and Duration
                if (elementId == 0x1549A966)
                {
                    reader.EnterContainer();

                    while (reader.ReadNext())
                    {
                        var infoElementId = reader.ElementId.EncodedValue;

                        // TimestampScale (0x2AD7B1)
                        if (infoElementId == 0x2AD7B1)
                        {
                            var value = reader.ReadUInt();
                            timestampScale = (long)value;
                        }

                        // Duration (0x4489)
                        else if (infoElementId == 0x4489)
                        {
                            var rawDuration = reader.ReadFloat();

                            // MKV duration is in TimestampScale units, convert to ticks
                            // Pre-calculate: ticksPerUnit = timestampScale / 100
                            long ticksPerUnit = timestampScale / 100;
                            durationTicks = (long)(rawDuration * ticksPerUnit);
                        }
                    }

                    reader.LeaveContainer();
                    foundInfo = true;

                    // Early exit if we already found Cues
                    if (foundCues)
                    {
                        break;
                    }
                }

                // Cues section (0x1C53BB6B) - contains keyframe positions
                else if (elementId == 0x1C53BB6B)
                {
                    // Pre-calculate tick multiplier for this file's TimestampScale
                    // Common case: timestampScale=1,000,000 (1ms) → ticksPerUnit=10,000
                    // Formula: (timestampScale nanoseconds) / (100 nanoseconds per tick) = ticks per unit
                    long ticksPerUnit = timestampScale / 100;

                    reader.EnterContainer();
                    while (reader.ReadNext())
                    {
                        var cuesElementId = reader.ElementId.EncodedValue;
                        // CuePoint (0xBB)
                        if (cuesElementId == 0xBB)
                        {
                            reader.EnterContainer();
                            while (reader.ReadNext())
                            {
                                var cuePointElementId = reader.ElementId.EncodedValue;
                                // CueTime (0xB3)
                                if (cuePointElementId == 0xB3)
                                {
                                    var cueTime = reader.ReadUInt();
                                    // Convert MKV timestamp to ticks using pre-calculated multiplier
                                    var keyframeTicks = (long)cueTime * ticksPerUnit;
                                    keyframes.Add(keyframeTicks);
                                }
                            }

                            reader.LeaveContainer();
                        }
                    }

                    reader.LeaveContainer();
                    foundCues = true;

                    // Early exit if we already found Info
                    if (foundInfo)
                    {
                        break;
                    }
                }
            }

            keyframes.Sort();
            return new KeyframeData(durationTicks, keyframes);
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Failed to parse EBML structure in file: {filePath}. Error: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Calculates the tick multiplier for a given TimestampScale.
    /// </summary>
    /// <param name="timestampScale">TimestampScale from MKV Info section (default: 1,000,000 nanoseconds).</param>
    /// <returns>Number of ticks per TimestampScale unit.</returns>
    /// <remarks>
    /// MKV timestamps are stored in units of TimestampScale nanoseconds.
    /// The conversion formula is: ticksPerUnit = timestampScale / 100.
    /// Common values:
    /// - timestampScale=1,000,000 (1ms) → 10,000 ticks per unit.
    /// - timestampScale=1,000 (1μs) → 10 ticks per unit.
    /// - timestampScale=1,000,000,000 (1s) → 10,000,000 ticks per unit.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when timestampScale is zero or negative.</exception>
    private static long CalculateTicksPerUnit(long timestampScale)
    {
        if (timestampScale <= 0)
        {
            throw new ArgumentException(
                $"TimestampScale must be positive, got: {timestampScale}",
                nameof(timestampScale));
        }

        // Convert nanoseconds to ticks: 1 tick = 100 nanoseconds
        return timestampScale / 100;
    }
}
