// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// One stored segment. Boundaries are measured in ticks (100 ns) relative to the
/// beginning of the media file, matching Jellyfin's <c>MediaSegment</c>; the
/// segment's <see cref="Id"/> is reused as the Jellyfin row id on sync so both
/// databases address the same segment by the same Guid.
/// </summary>
public class DbSegment
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegment"/> class with a
    /// freshly generated time-ordered id.
    /// </summary>
    /// <param name="itemId">Item (episode or movie) id.</param>
    /// <param name="type">Analysis mode the segment belongs to.</param>
    /// <param name="startTicks">Start time in ticks.</param>
    /// <param name="endTicks">End time in ticks.</param>
    /// <param name="source">Origin of the segment.</param>
    /// <param name="configHash">Configuration hash that produced the segment.</param>
    public DbSegment(Guid itemId, AnalysisMode type, long startTicks, long endTicks, SegmentSource source, string configHash = "")
    {
        Id = Guid.CreateVersion7();
        ItemId = itemId;
        Type = type;
        StartTicks = startTicks;
        EndTicks = endTicks;
        Source = source;
        State = SegmentState.Active;
        ConfigHash = configHash;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegment"/> class.
    /// EF materialization only — <see cref="Id"/> stays <see cref="Guid.Empty"/>.
    /// </summary>
    public DbSegment()
    {
    }

    /// <summary>
    /// Gets or sets the unique identifier. Client-generated (never by the database)
    /// and pushed as Jellyfin's <c>MediaSegment.Id</c> when the segment is synced.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the item (episode or movie) id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the analysis mode the segment belongs to.
    /// </summary>
    public AnalysisMode Type { get; set; }

    /// <summary>
    /// Gets or sets the start time in ticks.
    /// </summary>
    public long StartTicks { get; set; }

    /// <summary>
    /// Gets or sets the end time in ticks.
    /// </summary>
    public long EndTicks { get; set; }

    /// <summary>
    /// Gets or sets the origin of the segment.
    /// </summary>
    public SegmentSource Source { get; set; }

    /// <summary>
    /// Gets or sets the lifecycle state. <see cref="SegmentState.Suppressed"/> rows are
    /// tombstones: hidden from normal reads, never synced to Jellyfin, and they block
    /// re-insertion of overlapping automatic segments.
    /// </summary>
    public SegmentState State { get; set; }

    /// <summary>
    /// Gets or sets the configuration hash that produced this segment.
    /// Empty for user-provided and legacy-imported rows.
    /// </summary>
    public string ConfigHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC creation time. Stamped by
    /// <see cref="IntroSkipperDbContext"/> on insert when unset, so restored
    /// snapshots keep their original value.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the UTC time of the last modification. Stamped by
    /// <see cref="IntroSkipperDbContext"/> on every tracked update.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Converts the boundaries to a seconds-based <see cref="Segment"/>.
    /// </summary>
    /// <returns>A <see cref="Segment"/> object.</returns>
    internal Segment ToSegment()
        => new(ItemId, new TimeRange(TickConversions.ToSeconds(StartTicks), TickConversions.ToSeconds(EndTicks)));

    /// <summary>
    /// Creates a detached copy used to snapshot a row before deletion so it can be
    /// restored verbatim. Memberwise, so every persisted property — including columns
    /// added later — is carried automatically; all properties are value types or
    /// immutable strings, making the shallow copy exact.
    /// </summary>
    /// <returns>An untracked copy of this segment.</returns>
    internal DbSegment Clone() => (DbSegment)MemberwiseClone();
}
