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
            // Database layer: DbContext factories are the data-access seam (no repository layer).
            // The gated factory implementations await one-time initialization (legacy repair +
            // migrations + cache recovery) before handing out a context.
            serviceCollection.AddSingleton<DatabaseInitializer>();
            serviceCollection.AddHostedService<DatabaseInitializationService>();
            serviceCollection.AddDbContextFactory<IntroSkipperDbContext, GatedIntroSkipperDbContextFactory>((serviceProvider, options) =>
                IntroSkipperDatabase.ConfigureSqlite(
                    options,
                    IntroSkipperDatabase.GetSegmentDatabasePath(serviceProvider.GetRequiredService<IApplicationPaths>())));
            serviceCollection.AddDbContextFactory<DetectionCacheDbContext, GatedDetectionCacheDbContextFactory>((serviceProvider, options) =>
                IntroSkipperDatabase.ConfigureSqlite(
                    options,
                    IntroSkipperDatabase.GetCacheDatabasePath(serviceProvider.GetRequiredService<IApplicationPaths>())));

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
