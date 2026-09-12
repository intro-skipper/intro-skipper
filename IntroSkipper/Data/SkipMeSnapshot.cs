// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;

namespace IntroSkipper.Data;

/// <summary>
/// Validated, immutable SkipMe input for one queued item. Queue classification and analysis
/// use the same snapshot even if the source database changes during an analysis run.
/// </summary>
internal sealed class SkipMeSnapshot
{
    private readonly Guid _itemId;
    private readonly Dictionary<AnalysisMode, (long Start, long End)[]> _ranges;

    internal SkipMeSnapshot(Guid itemId, double duration, IReadOnlyList<MediaSegmentDto> segments)
    {
        _itemId = itemId;
        _ranges = segments
            .Where(segment => segment.ItemId == itemId && segment.StartTicks >= 0 && segment.EndTicks > segment.StartTicks
                && double.IsFinite(duration) && TimeSpan.FromTicks(segment.EndTicks).TotalSeconds <= duration)
            .Select(segment => (Mode: ToMode(segment.Type), segment.StartTicks, segment.EndTicks))
            .Where(segment => segment.Mode.HasValue)
            .GroupBy(segment => segment.Mode!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Select(segment => (segment.StartTicks, segment.EndTicks)).Distinct().Order().ToArray());
    }

    internal bool HasSegments(AnalysisMode mode) => _ranges.ContainsKey(mode);

    internal IReadOnlyList<Segment> GetSegments(AnalysisMode mode)
        => _ranges.TryGetValue(mode, out var ranges)
            ? ranges.Select(range => new Segment(_itemId, new TimeRange(TimeSpan.FromTicks(range.Start).TotalSeconds, TimeSpan.FromTicks(range.End).TotalSeconds))).ToArray()
            : [];

    internal string GetHashInput(AnalysisMode mode)
        => _ranges.TryGetValue(mode, out var ranges)
            ? string.Join(';', ranges.Select(range => FormattableString.Invariant($"{range.Start}:{range.End}")))
            : string.Empty;

    private static AnalysisMode? ToMode(MediaSegmentType type) => type switch
    {
        MediaSegmentType.Intro => AnalysisMode.Introduction,
        MediaSegmentType.Outro => AnalysisMode.Credits,
        MediaSegmentType.Recap => AnalysisMode.Recap,
        MediaSegmentType.Preview => AnalysisMode.Preview,
        MediaSegmentType.Commercial => AnalysisMode.Commercial,
        _ => null,
    };
}
