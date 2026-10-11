// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IntroSkipper.Db;

/// <summary>
/// Computes the opaque optimistic-concurrency token for an item's complete stored
/// segment image. Active rows and tombstones are included, so a filtered playback
/// view can never accidentally validate a stale editor image.
/// </summary>
internal static class SegmentRevision
{
    /// <summary>Computes a deterministic SHA-256 revision for the supplied rows.</summary>
    /// <param name="segments">All rows belonging to one item, including tombstones.</param>
    /// <returns>An opaque hexadecimal revision token.</returns>
    internal static string Compute(IEnumerable<DbSegment> segments)
    {
        var canonical = new StringBuilder();
        foreach (var segment in segments.OrderBy(s => s.Id))
        {
            canonical
                .Append(segment.Id.ToString("D")).Append('|')
                .Append((int)segment.Type).Append('|')
                .Append(segment.StartTicks.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(segment.EndTicks.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append((int)segment.Source).Append('|')
                .Append((int)segment.State).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
