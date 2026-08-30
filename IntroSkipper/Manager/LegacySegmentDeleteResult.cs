// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.Manager;

/// <summary>
/// Outcome of <see cref="MediaSegmentEditorService.DeleteLegacySegmentAsync"/>: the
/// segment was deleted, nothing matched the id on the item, or the row's actual type
/// contradicts the requested one (carried in <see cref="MismatchedType"/>; nothing was
/// touched). The default value is the not-found outcome.
/// </summary>
public readonly record struct LegacySegmentDeleteResult
{
    /// <summary>
    /// Gets the outcome for an id that matched no row on the item; nothing was touched.
    /// </summary>
    public static LegacySegmentDeleteResult NotFound => default;

    /// <summary>
    /// Gets the success outcome.
    /// </summary>
    public static LegacySegmentDeleteResult Deleted => new() { IsDeleted = true };

    /// <summary>
    /// Gets a value indicating whether the segment was deleted.
    /// </summary>
    public bool IsDeleted { get; private init; }

    /// <summary>
    /// Gets the row's actual segment type when it contradicts the requested type;
    /// otherwise <see langword="null" />.
    /// </summary>
    public MediaSegmentType? MismatchedType { get; private init; }

    /// <summary>
    /// Returns the type-contradiction outcome; nothing was touched.
    /// </summary>
    /// <param name="actualType">The row's actual segment type.</param>
    /// <returns>The outcome carrying <paramref name="actualType" />.</returns>
    public static LegacySegmentDeleteResult TypeMismatch(MediaSegmentType actualType) => new() { MismatchedType = actualType };
}
