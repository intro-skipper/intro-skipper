// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;

namespace IntroSkipper.Providers;

/// <summary>
/// Registry exposing all external <see cref="IMediaSegmentProvider" /> implementations that are
/// available via dependency-injection.
/// </summary>
public interface IExternalSegmentProviders
{
    /// <summary>
    /// Gets the external segment providers registered in the DI container.
    /// </summary>
    LibraryOptions Providers { get; }
}
