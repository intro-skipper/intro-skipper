// SPDX-FileCopyrightText: 2024 TwistedUmbrellaX
// SPDX-FileCopyrightText: 2024-2025 rlauuzo
// SPDX-FileCopyrightText: 2024 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Manager;
using IntroSkipper.Providers;
using IntroSkipper.Services;
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
            serviceCollection.AddHostedService<Entrypoint>();
            serviceCollection.AddSingleton<IMediaSegmentProvider, SegmentProvider>();
            serviceCollection.AddTransient<MediaSegmentUpdateManager>();
        }
    }
}
