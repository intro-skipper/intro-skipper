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
    /// movies only. The single definition behind every controller's item-type 404 guard.
    /// </summary>
    /// <param name="item">The item to classify; <c>null</c> (an ID missing from the library) is unsupported.</param>
    /// <returns><c>true</c> when the item is an <see cref="Episode"/> or <see cref="Movie"/>.</returns>
    internal static bool IsSupported([NotNullWhen(true)] BaseItem? item) => item is Episode or Movie;
}
