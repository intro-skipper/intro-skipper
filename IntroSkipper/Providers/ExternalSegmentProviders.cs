// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;

namespace IntroSkipper.Providers;

/// <summary>
/// Default implementation of <see cref="IExternalSegmentProviders"/> that simply exposes
/// all <see cref="IMediaSegmentProvider"/> instances known to the dependency-injection container.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ExternalSegmentProviders"/> class.
/// </remarks>
/// <param name="providers">All registered <see cref="IMediaSegmentProvider"/> instances.</param>
public sealed class ExternalSegmentProviders(IEnumerable<IMediaSegmentProvider> providers) : IExternalSegmentProviders
{
    private readonly LibraryOptions _providers = new()
        {
            DisabledMediaSegmentProviders = [.. providers.Where(p => p.Name != Plugin.Instance!.Name).Select(p => p.Name)]
        };

    /// <inheritdoc />
    public LibraryOptions Providers => _providers;
}
