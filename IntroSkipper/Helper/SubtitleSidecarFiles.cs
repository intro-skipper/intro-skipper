// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IntroSkipper.Helper;

/// <summary>
/// Finds adjacent subtitle files and incorporates their filesystem versions into the
/// queued media version so adding or updating a sidecar reopens analysis.
/// </summary>
internal static class SubtitleSidecarFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ass", ".idx", ".ssa", ".srt", ".sub", ".vtt",
    };

    /// <summary>
    /// Gets sidecars that can be passed to FFmpeg as text subtitle sources. A .sub next
    /// to a .idx is a VobSub image stream, not a MicroDVD text stream.
    /// </summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <returns>Readable text subtitle sidecars.</returns>
    public static string[] FindTextSources(string mediaPath)
    {
        var files = FindAll(mediaPath);
        var imageSubtitleStems = files
            .Where(path => string.Equals(Path.GetExtension(path), ".idx", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. files.Where(path =>
            !string.Equals(Path.GetExtension(path), ".idx", StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(Path.GetExtension(path), ".sub", StringComparison.OrdinalIgnoreCase)
                && imageSubtitleStems.Contains(Path.GetFileNameWithoutExtension(path))))];
    }

    /// <summary>
    /// Combines Jellyfin's media-file version with sidecar names, lengths and write times.
    /// Without sidecars, the original version is returned unchanged.
    /// </summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="mediaVersion">Jellyfin's media-file version.</param>
    /// <returns>A stable composite version, or the media version when no sidecars exist.</returns>
    public static long? FileVersion(string mediaPath, long? mediaVersion)
    {
        var files = FindAll(mediaPath);
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
