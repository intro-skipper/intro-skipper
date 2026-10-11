// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IntroSkipper.Data;

namespace IntroSkipper.Helper;

/// <summary>
/// Finds an episode's text subtitle files, next to the media file and in the item's internal
/// metadata folder, for subtitle extraction and for the file version that subtitle-detecting
/// modes record.
/// </summary>
internal static class SubtitleSidecarFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ass", ".idx", ".sami", ".smi", ".ssa", ".srt", ".sub", ".vtt",
    };

    /// <summary>
    /// Gets the sidecars that subtitle extraction passes to FFmpeg as text subtitle sources: the
    /// text sidecars in a selected language. A sidecar is a file named like the media file plus
    /// dot-separated segments, in the media file's folder or in
    /// <see cref="QueuedEpisode.InternalMetadataPath"/>, where Jellyfin saves downloaded
    /// subtitles when the library does not save them with the media. A .sub next to a .idx is a
    /// VobSub image stream, not a MicroDVD text stream. A sidecar's language comes from the
    /// segments between the media file's name and the extension (<c>Episode.de.srt</c>,
    /// <c>Episode.en.hi.srt</c>, <c>Episode.German.srt</c>); a sidecar without a language
    /// segment is always read.
    /// </summary>
    /// <remarks>
    /// Enumerates both folders synchronously. IO exceptions propagate.
    /// </remarks>
    /// <param name="episode">The episode whose media path and metadata folder are searched.</param>
    /// <param name="languages">The subtitle languages to read.</param>
    /// <returns>The selected text subtitle sidecars, ordered by path.</returns>
    public static string[] FindTextSources(QueuedEpisode episode, SubtitleLanguageSelection languages)
    {
        var stem = Path.GetFileNameWithoutExtension(episode.Path);
        var files = FindAll(episode, stem);
        var vobSubPairs = files
            .Where(path => string.Equals(Path.GetExtension(path), ".idx", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.ChangeExtension(path, null))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. files.Where(path =>
            !string.Equals(Path.GetExtension(path), ".idx", StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(Path.GetExtension(path), ".sub", StringComparison.OrdinalIgnoreCase)
                && vobSubPairs.Contains(Path.ChangeExtension(path, null)))
            && languages.IncludesSidecar(Path.GetFileNameWithoutExtension(path)[stem.Length..]))];
    }

    /// <summary>
    /// Combines Jellyfin's media-file version with the names, lengths and write times of the
    /// sidecars <see cref="FindTextSources"/> returns, so adding or rewriting one that subtitle
    /// extraction reads changes the version. VobSub sidecars and sidecars in an unselected
    /// language are never read and never change it.
    /// </summary>
    /// <remarks>
    /// Enumerates both sidecar folders and stats each text sidecar synchronously. IO
    /// exceptions propagate; the caller's per-episode verification owns recovery.
    /// </remarks>
    /// <param name="episode">The episode, carrying Jellyfin's media-file version in <see cref="QueuedEpisode.FileVersion"/>.</param>
    /// <param name="languages">The subtitle languages to read.</param>
    /// <returns>A stable composite version, or the media-file version unchanged when no sidecar is read.</returns>
    public static long? FileVersion(QueuedEpisode episode, SubtitleLanguageSelection languages)
    {
        var files = FindTextSources(episode, languages);
        if (files.Length == 0)
        {
            return episode.FileVersion;
        }

        var signature = new StringBuilder()
            .Append(episode.FileVersion?.ToString(CultureInfo.InvariantCulture))
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

    private static string[] FindAll(QueuedEpisode episode, string stem)
    {
        if (string.IsNullOrEmpty(stem))
        {
            return [];
        }

        // A literal prefix match, as Jellyfin's own external-file lookup does: a stem can hold
        // characters that a search pattern would treat as wildcards.
        return [.. new[] { Path.GetDirectoryName(episode.Path), episode.InternalMetadataPath }
            .OfType<string>()
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateFiles)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.Length > stem.Length
                    && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                    && name[stem.Length] == '.'
                    && Extensions.Contains(Path.GetExtension(path))
                    && !string.Equals(path, episode.Path, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)];
    }
}
