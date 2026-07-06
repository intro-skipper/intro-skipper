using IntroSkipper.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.MediaSegments;

namespace IntroSkipper.Manager;

/// <summary>
/// Shared rules for segment cardinality across plugin and Jellyfin media-segment storage.
/// </summary>
internal static class MediaSegmentRules
{
    /// <summary>
    /// Determines whether plugin DB segments may contain multiple rows for the supplied mode and queued media category.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="category">Queued media category.</param>
    /// <returns><see langword="true" /> when multiple segments are allowed.</returns>
    internal static bool AllowsMultipleSegments(AnalysisMode mode, QueuedMediaCategory category)
        => AllowsMultipleSegments(mode, category == QueuedMediaCategory.Movie);

    /// <summary>
    /// Determines whether plugin DB segments may contain multiple rows for the supplied mode and item.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="item">Media item.</param>
    /// <returns><see langword="true" /> when multiple segments are allowed.</returns>
    internal static bool AllowsMultipleSegments(AnalysisMode mode, BaseItem? item)
        => AllowsMultipleSegments(mode, item is Movie);

    /// <summary>
    /// Determines whether Jellyfin media segments may contain multiple rows for the supplied type and item.
    /// </summary>
    /// <param name="type">Jellyfin media segment type.</param>
    /// <param name="item">Media item.</param>
    /// <returns><see langword="true" /> when multiple segments are allowed.</returns>
    internal static bool AllowsMultipleSegments(MediaSegmentType type, BaseItem? item)
        => AllowsMultipleSegments(type, item is Movie);

    private static bool AllowsMultipleSegments(AnalysisMode mode, bool isMovie)
        => mode == AnalysisMode.Commercial || (mode == AnalysisMode.Credits && isMovie);

    private static bool AllowsMultipleSegments(MediaSegmentType type, bool isMovie)
        => type == MediaSegmentType.Commercial || (type == MediaSegmentType.Outro && isMovie);
}
