using ConfusedPolarBear.Plugin.IntroSkipper.Manager;
using ConfusedPolarBear.Plugin.IntroSkipper.Providers;
using ConfusedPolarBear.Plugin.IntroSkipper.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace ConfusedPolarBear.Plugin.IntroSkipper
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
            serviceCollection.AddSingleton<IMediaSegmentProvider, SegmentProvider>();
            serviceCollection.AddScoped<IMediaSegmentUpdateManager, MediaSegmentUpdateManager>();
        }
    }
}
