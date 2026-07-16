using System.Globalization;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Model.Configuration;

namespace IntroSkipper.Manager;

/// <summary>
/// Shared defaults for Intro Skipper media-segment provider operations.
/// </summary>
internal static class MediaSegmentProviderDefaults
{
    private const string ChapterProviderName = "Chapter Segments Provider";

    /// <summary>
    /// Gets the <see cref="LibraryOptions"/> used when querying or running external media-segment
    /// providers. The built-in "Chapter Segments Provider" is disabled so it is never re-run as part
    /// of an Intro Skipper refresh or editor operation. The returned instance is shared and must be
    /// treated as read-only.
    /// </summary>
    internal static LibraryOptions ExternalProviders { get; } = new()
    {
        DisabledMediaSegmentProviders = BuildDisabledProviderEntries(ChapterProviderName)
    };

    /// <summary>
    /// Gets the <see cref="LibraryOptions"/> used to remove Intro Skipper-owned segments while
    /// preserving segments produced by other external providers.
    /// </summary>
    internal static LibraryOptions ExternalProvidersWithoutIntroSkipper { get; } = new()
    {
        DisabledMediaSegmentProviders = BuildDisabledProviderEntries(ChapterProviderName, Plugin.ProviderName)
    };

    /// <summary>
    /// Computes the provider id Jellyfin derives from a media-segment provider name. Mirrors
    /// <c>MediaSegmentManager.GetProviderId</c>: the lowercased name hashed with MD5 and formatted
    /// as a 32-digit hex string.
    /// </summary>
    /// <param name="providerName">The media-segment provider display name.</param>
    /// <returns>The hashed provider id.</returns>
    internal static string GetProviderId(string providerName)
        => providerName.ToLowerInvariant()
            .GetMD5()
            .ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds <see cref="LibraryOptions.DisabledMediaSegmentProviders"/> entries for the given
    /// provider names. Jellyfin 10.11 matches entries against the hashed provider id while newer
    /// servers match the provider name case-insensitively, so both forms are included.
    /// </summary>
    /// <param name="providerNames">The media-segment provider display names to disable.</param>
    /// <returns>The disabled-provider entries.</returns>
    private static string[] BuildDisabledProviderEntries(params string[] providerNames)
        => [.. providerNames, .. providerNames.Select(GetProviderId)];
}
