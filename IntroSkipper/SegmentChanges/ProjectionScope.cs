// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Selects all item projections or one item.</summary>
public sealed record ProjectionScope
{
    private ProjectionScope(Guid? itemId)
    {
        ItemId = itemId;
    }

    /// <summary>Gets the item ID, or <see langword="null"/> for all items.</summary>
    public Guid? ItemId { get; }

    /// <summary>Gets the all-items scope.</summary>
    public static ProjectionScope All { get; } = new((Guid?)null);

    /// <summary>Creates a one-item scope.</summary>
    /// <param name="itemId">Non-empty item ID.</param>
    /// <returns>The item scope.</returns>
    public static ProjectionScope ForItem(Guid itemId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(itemId, Guid.Empty);
        return new(itemId);
    }
}
