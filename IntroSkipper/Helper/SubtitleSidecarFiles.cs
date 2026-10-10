// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IntroSkipper.Helper;

/// <summary>
/// Finds the text subtitle files next to a media file, for subtitle extraction and for the
/// file version that subtitle-detecting modes record.
/// </summary>
internal static class SubtitleSidecarFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ass", ".idx", ".ssa", ".srt", ".sub", ".vtt",
    };

    /// <summary>
    /// Gets the sidecars that subtitle extraction passes to FFmpeg as text subtitle sources: the
    /// text sidecars in a selected language. A .sub next to a .idx is a VobSub image stream, not
    /// a MicroDVD text stream. A sidecar's language comes from the dot-separated segments between
    /// the media file's name and the extension (<c>Episode.de.srt</c>, <c>Episode.en.hi.srt</c>);
    /// a sidecar without a language segment is always read.
    /// </summary>
    /// <remarks>
    /// Enumerates the media file's directory synchronously. IO exceptions propagate.
    /// </remarks>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="languages">The subtitle languages to read.</param>
    /// <returns>The selected text subtitle sidecars, ordered by path.</returns>
    public static string[] FindTextSources(string mediaPath, SubtitleLanguageSelection languages)
    {
        var files = FindAll(mediaPath);
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        var imageSubtitleStems = files
            .Where(path => string.Equals(Path.GetExtension(path), ".idx", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. files.Where(path =>
            !string.Equals(Path.GetExtension(path), ".idx", StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(Path.GetExtension(path), ".sub", StringComparison.OrdinalIgnoreCase)
                && imageSubtitleStems.Contains(Path.GetFileNameWithoutExtension(path)))
            && languages.IncludesSidecar(Path.GetFileNameWithoutExtension(path)[stem.Length..]))];
    }

    /// <summary>
    /// Combines Jellyfin's media-file version with the names, lengths and write times of the
    /// sidecars <see cref="FindTextSources"/> returns, so adding or rewriting one that subtitle
    /// extraction reads changes the version. VobSub sidecars and sidecars in an unselected
    /// language are never read and never change it.
    /// </summary>
    /// <remarks>
    /// Enumerates the media file's directory and stats each text sidecar synchronously. IO
    /// exceptions propagate; the caller's per-episode verification owns recovery.
    /// </remarks>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="mediaVersion">Jellyfin's media-file version.</param>
    /// <param name="languages">The subtitle languages to read.</param>
    /// <returns>A stable composite version, or <paramref name="mediaVersion"/> unchanged when no sidecar is read.</returns>
    public static long? FileVersion(string mediaPath, long? mediaVersion, SubtitleLanguageSelection languages)
    {
        var files = FindTextSources(mediaPath, languages);
        if (files.Length == 0)
        {
            return mediaVersion;
        }

        var signature = new StringBuilder()
            .Append(mediaVersion?.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        foreach (var path in files)
        {
            var file = new FileInfo(path);
            signature.Append(Path.GetFileName(path))
                .Append('|').Append(file.Length.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }

    private static string[] FindAll(string mediaPath)
    {
        var directory = Path.GetDirectoryName(mediaPath);
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(stem) || !Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(directory, stem + ".*", SearchOption.TopDirectoryOnly)
            .Where(path => Extensions.Contains(Path.GetExtension(path)) && !string.Equals(path, mediaPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)];
    }
}
