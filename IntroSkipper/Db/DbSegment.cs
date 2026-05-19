// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// All times are measured in seconds relative to the beginning of the media file.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DbSegment"/> class.
/// </remarks>
public class DbSegment
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegment"/> class.
    /// </summary>
    /// <param name="segment">The segment to initialize the instance with.</param>
    /// <param name="type">The type of analysis that was used to determine this segment.</param>
    /// <param name="isUserProvided">Whether this segment was provided by the user via the segment editor.</param>
    public DbSegment(Segment segment, AnalysisMode type, bool isUserProvided = false)
        : this(segment, type, isUserProvided, string.Empty)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegment"/> class.
    /// </summary>
    /// <param name="segment">The segment to initialize the instance with.</param>
    /// <param name="type">The type of analysis that was used to determine this segment.</param>
    /// <param name="isUserProvided">Whether this segment was provided by the user via the segment editor.</param>
    /// <param name="configHash">Configuration hash that produced this segment.</param>
    public DbSegment(Segment segment, AnalysisMode type, bool isUserProvided, string configHash)
    {
        ItemId = segment.EpisodeId;
        Start = segment.Start;
        End = segment.End;
        Type = type;
        IsUserProvided = isUserProvided;
        ConfigHash = configHash;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegment"/> class.
    /// </summary>
    public DbSegment()
    {
    }

    /// <summary>
    /// Gets or sets the unique identifier for the segment.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the episode id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the start time.
    /// </summary>
    public double Start { get; set; }

    /// <summary>
    /// Gets or sets the end time.
    /// </summary>
    public double End { get; set; }

    /// <summary>
    /// Gets the type of analysis that was used to determine this segment.
    /// </summary>
    public AnalysisMode Type { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether this segment was provided by the user via the segment editor,
    /// and must not be overwritten by automatic analysis.
    /// </summary>
    public bool IsUserProvided { get; set; }

    /// <summary>
    /// Gets or sets the configuration hash that produced this segment.
    /// </summary>
    public string ConfigHash { get; set; } = string.Empty;

    /// <summary>
    /// Converts the instance to a <see cref="Segment"/> object.
    /// </summary>
    /// <returns>A <see cref="Segment"/> object.</returns>
    internal Segment ToSegment()
    {
        return new Segment(ItemId, new TimeRange(Start, End));
    }
}
