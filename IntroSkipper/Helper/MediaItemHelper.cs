// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace IntroSkipper.Helper;

/// <summary>
/// Classification of the library item kinds the plugin manages segments for.
/// </summary>
internal static class MediaItemHelper
{
    /// <summary>
    /// Determines whether the plugin manages segments for the item — episodes and
    /// movies only. The single definition of this check: the controllers' item-type
    /// 404 guards and the segment provider's Supports check all route here.
    /// </summary>
    /// <param name="item">The item to classify; <c>null</c> (an ID missing from the library) is unsupported.</param>
    /// <returns><c>true</c> when the item is an <see cref="Episode"/> or <see cref="Movie"/>.</returns>
    internal static bool IsSupported([NotNullWhen(true)] BaseItem? item) => item is Episode or Movie;

    /// <summary>
    /// Resolves an item id to its library item when the plugin manages segments for
    /// it — the shared form of the controllers' resolve-then-guard step.
    /// </summary>
    /// <param name="itemId">The item id to resolve.</param>
    /// <returns>The supported item, or <c>null</c> when the id is unknown or the item kind is unsupported.</returns>
    internal static BaseItem? FindSupported(Guid itemId)
    {
        var item = Plugin.Instance!.GetItem(itemId);
        return IsSupported(item) ? item : null;
    }
}
