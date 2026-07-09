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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
            // The facades own database initialization, so they are constructed over
            // ungated inner factories: their initialization cores create contexts, and
            // handing them the gated factories below would deadlock the init gate
            // against itself.
            serviceCollection.AddSingleton<IIntroSkipperDatabase>(serviceProvider => new IntroSkipperDatabase(
                CreateUngatedSegmentContextFactory(serviceProvider),
                serviceProvider.GetRequiredService<ILogger<IntroSkipperDatabase>>()));
            serviceCollection.AddSingleton<IDetectionCacheDatabase>(serviceProvider => new DetectionCacheDatabase(
                CreateUngatedCacheContextFactory(serviceProvider),
                serviceProvider.GetRequiredService<ILogger<DetectionCacheDatabase>>()));

            // Structural ordering gate (defense in depth): the *registered* factories are
            // decorators that run the corresponding facade's one-shot initialization gate
            // before handing out a context, so even a future consumer that resolves the
            // raw factory instead of a facade cannot query an unmigrated database. No
            // production code resolves these today — every consumer goes through the
            // facades, which use the ungated inner factories above.
            serviceCollection.Replace(ServiceDescriptor.Singleton<IDbContextFactory<IntroSkipperDbContext>>(serviceProvider =>
                new GatedIntroSkipperDbContextFactory(
                    serviceProvider.GetRequiredService<IIntroSkipperDatabase>(),
                    CreateUngatedSegmentContextFactory(serviceProvider))));
            serviceCollection.Replace(ServiceDescriptor.Singleton<IDbContextFactory<DetectionCacheDbContext>>(serviceProvider =>
                new GatedDetectionCacheDbContextFactory(
                    serviceProvider.GetRequiredService<IDetectionCacheDatabase>(),
                    CreateUngatedCacheContextFactory(serviceProvider))));

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

        private static DelegateDbContextFactory<IntroSkipperDbContext> CreateUngatedSegmentContextFactory(IServiceProvider serviceProvider)
        {
            var options = serviceProvider.GetRequiredService<DbContextOptions<IntroSkipperDbContext>>();
            return new DelegateDbContextFactory<IntroSkipperDbContext>(() => new IntroSkipperDbContext(options));
        }

        private static DelegateDbContextFactory<DetectionCacheDbContext> CreateUngatedCacheContextFactory(IServiceProvider serviceProvider)
        {
            var options = serviceProvider.GetRequiredService<DbContextOptions<DetectionCacheDbContext>>();
            return new DelegateDbContextFactory<DetectionCacheDbContext>(() => new DetectionCacheDbContext(options));
        }
    }
}
