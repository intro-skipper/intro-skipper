using System;
using System.Collections.Generic;
using System.IO;
using IntroSkipper.Data;
using NEbml.Core;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Extracts keyframe data from MKV files by parsing EBML structure.
/// MKV files store timestamps in units of TimestampScale nanoseconds, which are converted to seconds.
/// </summary>
/// <remarks>
/// This implementation directly parses the MKV container format to extract.
/// </remarks>
public class MkvKeyframeExtractor : IKeyframeExtractor
{
    /// <summary>
    /// Extracts keyframe data from an MKV file.
    /// </summary>
    /// <param name="filePath">Path to the MKV file.</param>
    /// <returns>KeyframeData with duration and keyframes in seconds.</returns>
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
            double duration = 0.0;

            // Pre-allocate list with typical capacity
            var keyframes = new List<double>(capacity: 2000);
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

                            // MKV duration is in TimestampScale units, convert to seconds
                            duration = (rawDuration * timestampScale) / 1_000_000_000.0;
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
                    double conversionFactor = timestampScale / 1_000_000_000.0;
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
                                    var keyframeSeconds = (long)cueTime * conversionFactor;
                                    keyframes.Add(keyframeSeconds);
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
            return new KeyframeData(duration, keyframes);
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
    /// Converts MKV timestamp (in TimestampScale units) to seconds.
    /// </summary>
    /// <param name="timestamp">Raw MKV timestamp value in TimestampScale units.</param>
    /// <param name="timestampScale">TimestampScale from MKV Info section (default: 1,000,000 nanoseconds).</param>
    /// <returns>Timestamp in seconds.</returns>
    /// <remarks>
    /// MKV timestamps are stored in units of TimestampScale nanoseconds.
    /// The conversion formula is: seconds = (timestamp × timestampScale) / 1,000,000,000.
    /// Example: timestamp=5000, timestampScale=1,000,000 → 5.0 seconds.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when timestampScale is zero or negative.</exception>
    private static double ScaleToSeconds(long timestamp, long timestampScale)
    {
        if (timestampScale <= 0)
        {
            throw new ArgumentException(
                $"TimestampScale must be positive, got: {timestampScale}",
                nameof(timestampScale));
        }

        // MKV timestamps are in units of TimestampScale nanoseconds
        // Convert to seconds: (timestamp × timestampScale) / 1,000,000,000
        return (timestamp * timestampScale) / 1_000_000_000.0;
    }
}
