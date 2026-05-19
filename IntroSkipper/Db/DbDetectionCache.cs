// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics.CodeAnalysis;
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
    /// <param name="variant">The versioned cache variant identifier.</param>
    /// <param name="data">The Brotli-compressed, UTF-8 JSON detection data.</param>
    /// <param name="start">The start time of the analyzed range.</param>
    /// <param name="end">The end time of the analyzed range.</param>
    [SetsRequiredMembers]
    public DbDetectionCache(Guid itemId, AnalysisMode mode, CacheEntryType type, string variant, byte[] data, double start = 0, double end = 0)
    {
        ItemId = itemId;
        Mode = mode;
        Type = type;
        Variant = variant;
        Data = data;
        Start = start;
        End = end;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbDetectionCache"/> class using the default variant for tests and legacy callers.
    /// </summary>
    /// <param name="itemId">The episode identifier.</param>
    /// <param name="mode">The analysis mode.</param>
    /// <param name="type">The type of detection data.</param>
    /// <param name="data">The Brotli-compressed, UTF-8 JSON detection data.</param>
    /// <param name="start">The start time of the analyzed range.</param>
    /// <param name="end">The end time of the analyzed range.</param>
    [SetsRequiredMembers]
    public DbDetectionCache(Guid itemId, AnalysisMode mode, CacheEntryType type, byte[] data, double start = 0, double end = 0)
        : this(itemId, mode, type, GetDefaultVariant(type, end), data, start, end)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbDetectionCache"/> class.
    /// </summary>
    [SetsRequiredMembers]
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
    /// Gets or sets the versioned cache variant identifier.
    /// </summary>
    public required string Variant { get; set; } = string.Empty;

    /// <summary>
    /// Gets the start time of the analyzed range (in seconds).
    /// </summary>
    /// <value>The range start in seconds. For chromaprint entries, this is the fingerprint start time (0 for introductions).</value>
    public double Start { get; private set; }

    /// <summary>
    /// Gets the end time of the analyzed range (in seconds).
    /// </summary>
    /// <value>The range end in seconds. For chromaprint entries, this is the fingerprint end time.</value>
    public double End { get; private set; }

    /// <summary>
    /// Gets or sets the Brotli-compressed, UTF-8 JSON detection data.
    /// </summary>
    /// <value>The cached data as a compressed BLOB. The decompressed JSON shape depends on <see cref="Type"/>:
    /// <see cref="CacheEntryType.Chromaprint"/> stores <c>uint[]</c>,
    /// <see cref="CacheEntryType.Silence"/> stores <c>TimeRange[]</c>,
    /// <see cref="CacheEntryType.BlackFrame"/> stores <c>BlackFrame[]</c>,
    /// <see cref="CacheEntryType.Keyframe"/> stores <c>double[]</c>.
    /// </value>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "EF Core requires byte[] for BLOB column mapping.")]
    public byte[] Data { get; set; } = [];

    private static string GetDefaultVariant(CacheEntryType type, double end)
        => type switch
        {
            CacheEntryType.Chromaprint => IntroSkipper.FFmpeg.DetectionCacheVariant.Chromaprint(),
            CacheEntryType.Silence => IntroSkipper.FFmpeg.DetectionCacheVariant.Silence(-50),
            CacheEntryType.Keyframe => IntroSkipper.FFmpeg.DetectionCacheVariant.Keyframe(),
            CacheEntryType.BlackFrame when end == 0 => IntroSkipper.FFmpeg.DetectionCacheVariant.BlackFrameCredits(28),
            CacheEntryType.BlackFrame => IntroSkipper.FFmpeg.DetectionCacheVariant.BlackFrameRange(28),
            _ => type.ToString(),
        };
}
