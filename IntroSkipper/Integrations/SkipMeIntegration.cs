// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using Microsoft.Extensions.DependencyInjection;

namespace IntroSkipper.Integrations;

/// <summary>
/// Optional, versioned registration boundary for the SkipMe plugin. The registration
/// signature uses only shared Jellyfin and framework types, so SkipMe can discover it
/// without referencing or distributing the Intro Skipper assembly.
/// </summary>
public sealed class SkipMeIntegration
{
    private readonly IMediaSegmentProvider _provider;

    private SkipMeIntegration(IMediaSegmentProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Registers SkipMe as an authoritative analyzer source instead of a Jellyfin provider.
    /// The caller must retain its standalone provider when this entry point is unavailable,
    /// and must not register that provider with Jellyfin when this registration succeeds.
    /// </summary>
    /// <param name="services">Jellyfin's shared service collection.</param>
    /// <param name="providerFactory">Creates the local SkipMe segment reader after DI is built.</param>
    public static void RegisterV1(IServiceCollection services, Func<IServiceProvider, IMediaSegmentProvider> providerFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(providerFactory);
        services.AddSingleton(serviceProvider => new SkipMeIntegration(providerFactory(serviceProvider)));
    }

    internal async Task<SkipMeSnapshot> ReadAsync(BaseItem item, double duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _provider.Supports(item).ConfigureAwait(false))
        {
            return new SkipMeSnapshot(item.Id, duration, []);
        }

        var segments = await _provider.GetMediaSegments(new MediaSegmentGenerationRequest { ItemId = item.Id, ExistingSegments = [] }, cancellationToken).ConfigureAwait(false);
        return new SkipMeSnapshot(item.Id, duration, segments);
    }
}
