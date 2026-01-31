// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using IntroSkipper.Db;
using IntroSkipper.Filters;
using IntroSkipper.Manager;
using IntroSkipper.Providers;
using IntroSkipper.Repositories;
using IntroSkipper.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
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
            // Database context
            serviceCollection.AddScoped((serviceProvider) =>
            {
                var dbPath = Plugin.Instance?.DbPath ?? throw new InvalidOperationException("Plugin not initialized");
                return new IntroSkipperDbContext(dbPath);
            });

            serviceCollection.AddSingleton<MediaSegmentsFirstEpisodeFilter>();
            serviceCollection.Configure<MvcOptions>(options =>
            {
                options.Filters.AddService<MediaSegmentsFirstEpisodeFilter>(order: 0);
            });

            // Repositories
            serviceCollection.AddScoped<ISegmentRepository, SegmentRepository>();
            serviceCollection.AddScoped<IOutboxRepository, OutboxRepository>();
            serviceCollection.AddScoped<ISeasonRepository, SeasonRepository>();

            // Managers
            serviceCollection.AddSingleton<MediaSegmentUpdateManager>();

            // Services
            serviceCollection.AddScoped<ISegmentService, SegmentService>();

            // Background services
            serviceCollection.AddHostedService<Entrypoint>();
            serviceCollection.AddHostedService<OutboxProcessorService>();

            // Providers
            serviceCollection.AddSingleton<IMediaSegmentProvider, SegmentProvider>();
        }
    }
}
