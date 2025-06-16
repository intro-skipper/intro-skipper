// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using IntroSkipper.Manager;
using IntroSkipper.Providers;
using IntroSkipper.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace IntroSkipper
{
    /// <summary>
    /// Register Intro Skipper services.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddHostedService<AutoSkip>();
            serviceCollection.AddHostedService<Entrypoint>();

            // Register all external segment providers (already added individually elsewhere).
            serviceCollection.AddSingleton<IMediaSegmentProvider, SegmentProvider>();

            // Register registry exposing the providers to consumers.
            serviceCollection.AddSingleton<IExternalSegmentProviders, ExternalSegmentProviders>();

            // Manager depends on IExternalSegmentProviders, use transient lifetime.
            serviceCollection.AddTransient<MediaSegmentUpdateManager>();
        }
    }
}
