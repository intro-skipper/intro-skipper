using MediaBrowser.Model.Configuration;

namespace IntroSkipper.Manager;

/// <summary>
/// Shared defaults for Intro Skipper media-segment provider operations.
/// </summary>
internal static class MediaSegmentProviderDefaults
{
    /// <summary>
    /// Gets the <see cref="LibraryOptions"/> used when querying or running external media-segment
    /// providers. The built-in "Chapter Segments Provider" is disabled so it is never re-run as part
    /// of an Intro Skipper refresh or editor operation. The returned instance is shared and must be
    /// treated as read-only.
    /// </summary>
    internal static LibraryOptions ExternalProviders { get; } = new()
    {
        DisabledMediaSegmentProviders = ["Chapter Segments Provider"]
    };
}
