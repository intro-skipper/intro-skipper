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
using Microsoft.Extensions.Logging;

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
            // Database layer. Hand-rolled IDbContextFactory implementations are registered instead
            // of EF's AddDbContextFactory so no EF internal services leak into the shared Jellyfin
            // service collection (see docs/db-redesign/theory-a.md for the trade-off discussion).
            serviceCollection.AddSingleton<IDbContextFactory<IntroSkipperDbContext>>(serviceProvider =>
                new SegmentDbContextFactory(PluginDatabasePaths.GetSegmentDbPath(serviceProvider.GetRequiredService<IApplicationPaths>())));
            serviceCollection.AddSingleton<IDbContextFactory<DetectionCacheDbContext>>(serviceProvider =>
                new DetectionCacheDbContextFactory(PluginDatabasePaths.GetCacheDbPath(serviceProvider.GetRequiredService<IApplicationPaths>())));
            serviceCollection.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
            serviceCollection.AddSingleton<ISegmentStore>(serviceProvider => new SegmentStore(
                serviceProvider.GetRequiredService<IDbContextFactory<IntroSkipperDbContext>>(),
                serviceProvider.GetRequiredService<IDatabaseInitializer>()));
            serviceCollection.AddSingleton<ISeasonStateStore>(serviceProvider => new SeasonStateStore(
                serviceProvider.GetRequiredService<IDbContextFactory<IntroSkipperDbContext>>(),
                serviceProvider.GetRequiredService<IDatabaseInitializer>()));
            serviceCollection.AddSingleton<IDetectionCacheStore>(serviceProvider => new DetectionCacheStore(
                serviceProvider.GetRequiredService<IDbContextFactory<DetectionCacheDbContext>>(),
                serviceProvider.GetRequiredService<IDatabaseInitializer>()));
            serviceCollection.AddSingleton<ISegmentUpdateService>(serviceProvider => new SegmentUpdateService(
                serviceProvider.GetRequiredService<ISegmentStore>(),
                serviceProvider.GetRequiredService<ILogger<SegmentUpdateService>>()));

            // Registered before Entrypoint so database initialization starts first.
            serviceCollection.AddHostedService<DatabaseStartupService>();

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
