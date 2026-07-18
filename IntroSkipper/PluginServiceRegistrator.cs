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
        private static readonly SqlitePragmaInterceptor _pragmaInterceptor = new();

        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Database layer. Non-pooled factories: the plugin's query rate is far too low
            // for pooling to matter, and pooling would forbid the string-path constructor
            // that the design-time factory, the rebuild flow and the tests rely on.
            serviceCollection.AddDbContextFactory<IntroSkipperDbContext>((serviceProvider, options) =>
                options.UseSqlite($"Data Source={IntroSkipperDatabasePaths.GetSegmentDatabasePath(serviceProvider.GetRequiredService<IApplicationPaths>())}")
                    .AddInterceptors(_pragmaInterceptor));
            serviceCollection.AddDbContextFactory<DetectionCacheDbContext>((serviceProvider, options) =>
                options.UseSqlite($"Data Source={IntroSkipperDatabasePaths.GetDetectionCacheDatabasePath(serviceProvider.GetRequiredService<IApplicationPaths>())}")
                    .AddInterceptors(_pragmaInterceptor));
            // The facades own database initialization via their internal retryable
            // gates; every consumer goes through a facade.
            serviceCollection.AddSingleton<IIntroSkipperDatabase, IntroSkipperDatabase>();
            serviceCollection.AddSingleton<IDetectionCacheDatabase, DetectionCacheDatabase>();

            // Registered before Entrypoint so migrations are warmed as the first hosted
            // service; the facades' internal gate still guarantees ordering for any
            // request that arrives earlier.
            serviceCollection.AddHostedService<IntroSkipperDatabaseInitializer>();

            serviceCollection.AddHostedService<Entrypoint>();
            serviceCollection.AddSingleton<IDetectionCacheService, DetectionCacheService>();
            serviceCollection.AddSingleton<IFFmpegService, FFmpegService>();
            serviceCollection.AddSingleton<IMediaSegmentProvider, SegmentProvider>();
            serviceCollection.AddSingleton<IMediaSegmentRefresher, MediaSegmentRefreshService>();
            serviceCollection.AddTransient<MediaSegmentEditorService>();
            serviceCollection.AddSingleton<MediaSegmentsFirstEpisodeFilter>();
            serviceCollection.Configure<MvcOptions>(options =>
            {
                options.Conventions.Add(new MediaSegmentsFilterConvention());
            });
        }
    }
}
