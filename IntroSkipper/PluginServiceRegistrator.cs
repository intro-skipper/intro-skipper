// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using MediaBrowser.Controller;
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
            serviceCollection.AddHostedService<AutoSkipCredits>();
            serviceCollection.AddHostedService<Entrypoint>();
        }
    }
}
