// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Filters;
using IntroSkipper.Manager;
using IntroSkipper.Providers;
using IntroSkipper.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
            // Database layer. Non-pooled factories: the plugin's query rate is far too low
            // for pooling to matter, and pooling would forbid the string-path constructor
            // that the design-time factory, the rebuild flow and the tests rely on.
            serviceCollection.AddDbContextFactory<IntroSkipperDbContext>((serviceProvider, options) =>
                SqlitePragmas.Configure(options, IntroSkipperDatabasePaths.GetSegmentDatabasePath(serviceProvider.GetRequiredService<IApplicationPaths>())));
            serviceCollection.AddDbContextFactory<DetectionCacheDbContext>((serviceProvider, options) =>
                SqlitePragmas.Configure(options, IntroSkipperDatabasePaths.GetDetectionCacheDatabasePath(serviceProvider.GetRequiredService<IApplicationPaths>())));
            // The facades own database initialization via their internal retryable
            // gates; every consumer goes through a facade.
            serviceCollection.AddSingleton<IIntroSkipperDatabase, IntroSkipperDatabase>();
            serviceCollection.AddSingleton<IDetectionCacheDatabase, DetectionCacheDatabase>();

            // Registered before Entrypoint so migrations are warmed as the first hosted
            // service; the facades' internal gate still guarantees ordering for any
            // request that arrives earlier.
            serviceCollection.AddHostedService<IntroSkipperDatabaseInitializer>();

            serviceCollection.AddHostedService<Entrypoint>();
            // Owns the shared dependency set of the per-run analysis objects
            // (QueueManager, BaseItemAnalyzerTask), which are stateful per run and
            // therefore created fresh by this factory instead of being singletons.
            serviceCollection.AddSingleton<AnalyzerTaskFactory>();
            serviceCollection.AddSingleton<IDetectionCacheService, DetectionCacheService>();
            serviceCollection.AddSingleton<IFFmpegService, FFmpegService>();
            // Shared plugin-to-Jellyfin segment conversion plus the direct writer into
            // Jellyfin's MediaSegments table; the provider stays registered so
            // Jellyfin-initiated runs converge to the same data.
            serviceCollection.AddSingleton<SegmentDtoFactory>();
            serviceCollection.AddSingleton<IJellyfinSegmentStore, JellyfinSegmentStore>();
            // The one locked write path into the Jellyfin mirror; both the editor service
            // and the refresh service go through it.
            serviceCollection.AddSingleton<MediaSegmentMirror>();
            serviceCollection.AddSingleton<IMediaSegmentProvider, SegmentProvider>();
            serviceCollection.AddSingleton<IMediaSegmentRefresher, MediaSegmentRefreshService>();
            // Singleton: the editor service serializes all interactive mutations per item
            // on its own striped lock, which only works when every request shares it.
            serviceCollection.AddSingleton<MediaSegmentEditorService>();
            serviceCollection.AddSingleton<MediaSegmentsFirstEpisodeFilter>();
            serviceCollection.Configure<MvcOptions>(options =>
            {
                options.Conventions.Add(new MediaSegmentsFilterConvention());
            });
        }
    }
}
