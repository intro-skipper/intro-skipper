// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Represents a cached FFmpeg detection result stored in the detection cache database.
/// </summary>
public class DbDetectionCache
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbDetectionCache"/> class.
    /// </summary>
    /// <param name="itemId">The episode identifier.</param>
    /// <param name="mode">The analysis mode.</param>
    /// <param name="type">The type of detection data.</param>
    /// <param name="data">The JSON-serialized detection data.</param>
    /// <param name="start">The start time of the analyzed range.</param>
    /// <param name="end">The end time of the analyzed range.</param>
    public DbDetectionCache(Guid itemId, AnalysisMode mode, CacheEntryType type, string data, double start = 0, double end = 0)
    {
        ItemId = itemId;
        Mode = mode;
        Type = type;
        Data = data;
        Start = start;
        End = end;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbDetectionCache"/> class.
    /// </summary>
    public DbDetectionCache()
    {
    }

    /// <summary>
    /// Gets or sets the unique identifier for the cache entry.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets the episode identifier.
    /// </summary>
    public Guid ItemId { get; private set; }

    /// <summary>
    /// Gets the analysis mode (introduction or credits).
    /// </summary>
    public AnalysisMode Mode { get; private set; }

    /// <summary>
    /// Gets the type of detection data stored in this entry.
    /// </summary>
    public CacheEntryType Type { get; private set; }

    /// <summary>
    /// Gets the start time of the analyzed range (in seconds).
    /// </summary>
    /// <value>The range start, or 0 when not applicable (e.g., chromaprint entries).</value>
    public double Start { get; private set; }

    /// <summary>
    /// Gets the end time of the analyzed range (in seconds).
    /// </summary>
    /// <value>The range end, or 0 when not applicable (e.g., chromaprint entries).</value>
    public double End { get; private set; }

    /// <summary>
    /// Gets or sets the JSON-serialized detection data.
    /// </summary>
    /// <value>The cached data as a JSON string. The shape depends on <see cref="Type"/>:
    /// <see cref="CacheEntryType.Chromaprint"/> stores <c>uint[]</c>,
    /// <see cref="CacheEntryType.Silence"/> stores <c>TimeRange[]</c>,
    /// <see cref="CacheEntryType.BlackFrame"/> stores <c>BlackFrame[]</c>,
    /// <see cref="CacheEntryType.Keyframe"/> stores <c>double[]</c>.
    /// </value>
    public string Data { get; set; } = string.Empty;
}
