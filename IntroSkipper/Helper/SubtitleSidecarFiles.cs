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
    /// Lists both folders synchronously. IO exceptions propagate.
    /// </remarks>
    /// <param name="episode">The episode whose media path and metadata folder are searched.</param>
    /// <param name="languages">The subtitle languages to read.</param>
    /// <returns>The selected text subtitle sidecars, ordered by path.</returns>
    public static string[] FindTextSources(QueuedEpisode episode, SubtitleLanguageSelection languages)
        => [.. FindTextFiles(episode, languages, new FolderListings()).Select(file => file.FullName)];

    /// <summary>
    /// Combines Jellyfin's media-file version with the names, lengths and write times of the
    /// sidecars <see cref="FindTextSources"/> returns, so adding or rewriting one that subtitle
    /// extraction reads changes the version. VobSub sidecars and sidecars in an unselected
    /// language are never read and never change it.
    /// </summary>
    /// <remarks>
    /// Lists both sidecar folders through <paramref name="folders"/> synchronously, and takes
    /// lengths and write times from the listing. IO exceptions propagate; the caller's
    /// per-episode verification owns recovery.
    /// </remarks>
    /// <param name="episode">The episode, carrying Jellyfin's media-file version in <see cref="QueuedEpisode.FileVersion"/>.</param>
    /// <param name="languages">The subtitle languages to read.</param>
    /// <param name="folders">The folder listings the verification pass shares.</param>
    /// <returns>A stable composite version, or the media-file version unchanged when no sidecar is read.</returns>
    public static long? FileVersion(QueuedEpisode episode, SubtitleLanguageSelection languages, FolderListings folders)
    {
        var files = FindTextFiles(episode, languages, folders);
        if (files.Length == 0)
        {
            return episode.FileVersion;
        }

        var signature = new StringBuilder()
            .Append(episode.FileVersion?.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        foreach (var file in files)
        {
            signature.Append(file.Name)
                .Append('|').Append(file.Length.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }

    private static FileInfo[] FindTextFiles(QueuedEpisode episode, SubtitleLanguageSelection languages, FolderListings folders)
    {
        var stem = Path.GetFileNameWithoutExtension(episode.Path);
        var files = FindAll(episode, stem, folders);
        var vobSubPairs = files
            .Where(file => string.Equals(file.Extension, ".idx", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.ChangeExtension(file.FullName, null))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. files.Where(file =>
            !string.Equals(file.Extension, ".idx", StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(file.Extension, ".sub", StringComparison.OrdinalIgnoreCase)
                && vobSubPairs.Contains(Path.ChangeExtension(file.FullName, null)))
            && languages.IncludesSidecar(Path.GetFileNameWithoutExtension(file.Name)[stem.Length..]))];
    }

    private static FileInfo[] FindAll(QueuedEpisode episode, string stem, FolderListings folders)
    {
        if (string.IsNullOrEmpty(stem))
        {
            return [];
        }

        // A literal prefix match, as Jellyfin's own external-file lookup does: a stem can hold
        // characters that a search pattern would treat as wildcards.
        return [.. new[] { Path.GetDirectoryName(episode.Path), episode.InternalMetadataPath }
            .OfType<string>()
            .SelectMany(folders.List)
            .Where(file => file.Name.Length > stem.Length
                && file.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                && file.Name[stem.Length] == '.'
                && Extensions.Contains(file.Extension)
                && !string.Equals(file.FullName, episode.Path, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.FullName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The folder listings one verification pass shares. The episodes of a season usually share
    /// a folder, which is then listed once rather than once per episode; over a network share,
    /// every listing costs round trips. A folder is listed the first time it is asked for and
    /// never again, so a file written later counts from the next pass, which makes new listings.
    /// </summary>
    internal sealed class FolderListings
    {
        private readonly Dictionary<string, FileInfo[]> _listings = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the files in a folder, with the lengths and write times the listing reports.
        /// </summary>
        /// <remarks>
        /// Lists the folder synchronously on first use. IO exceptions propagate, and a folder
        /// that failed is listed again when next asked for.
        /// </remarks>
        /// <param name="folder">The folder's path.</param>
        /// <returns>The folder's files, or none when the folder does not exist.</returns>
        public FileInfo[] List(string folder)
        {
            if (!_listings.TryGetValue(folder, out var files))
            {
                var directory = new DirectoryInfo(folder);
                files = directory.Exists ? [.. directory.EnumerateFiles()] : [];
                _listings[folder] = files;
            }

            return files;
        }
    }
}
