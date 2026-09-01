// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Filters;
using IntroSkipper.Manager;
using IntroSkipper.Providers;
using IntroSkipper.SegmentChanges;
using IntroSkipper.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
            // gates; every consumer goes through a facade. The segment facade is
            // registered once and forwarded to both of its interfaces so the domain
            // surface and the projection journal share one gate and one instance.
            serviceCollection.AddSingleton<IntroSkipperDatabase>();
            serviceCollection.AddSingleton<IIntroSkipperDatabase>(serviceProvider => serviceProvider.GetRequiredService<IntroSkipperDatabase>());
            serviceCollection.AddSingleton<ISegmentProjectionJournal>(serviceProvider => serviceProvider.GetRequiredService<IntroSkipperDatabase>());
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
            // Every plugin write into Jellyfin's MediaSegments table goes through the
            // mirror: per-item locked syncs and targeted deletes, plus a bulk cleanup
            // that takes each lock stripe in turn.
            serviceCollection.AddSingleton<MediaSegmentMirror>();
            serviceCollection.AddSingleton<IMediaSegmentProvider, SegmentProvider>();
            serviceCollection.AddSingleton<IMediaSegmentRefresher, MediaSegmentRefreshService>();
            // The mutation stripes serialize all interactive mutations per item —
            // apply and projection alike — which only works when every request
            // shares the singleton.
            serviceCollection.AddSingleton<SegmentMutationLocks>();
            // Live view of the mirroring flag plus its toggle event; hosted so it can
            // subscribe to plugin configuration changes.
            serviceCollection.AddSingleton<MediaSegmentMirrorPolicyService>();
            serviceCollection.AddSingleton<IMediaSegmentMirrorPolicy>(serviceProvider => serviceProvider.GetRequiredService<MediaSegmentMirrorPolicyService>());
            serviceCollection.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<MediaSegmentMirrorPolicyService>());
            // Durable segment changes: the coordinator commits intents through the
            // facade and retries journaled projection work; hosted for the retry loop.
            // TryAdd: the service collection is Jellyfin's shared server-wide
            // container, so an unconditional registration would claim (or cede to a
            // later plugin) the global TimeProvider slot for every consumer.
            serviceCollection.TryAddSingleton(TimeProvider.System);
            serviceCollection.AddSingleton<ISegmentProjectionAdapter, JellyfinSegmentProjectionAdapter>();
            serviceCollection.AddSingleton<SegmentChange>();
            serviceCollection.AddSingleton<ISegmentChange>(serviceProvider => serviceProvider.GetRequiredService<SegmentChange>());
            serviceCollection.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<SegmentChange>());
            serviceCollection.AddSingleton<MediaSegmentsFirstEpisodeFilter>();
            serviceCollection.Configure<MvcOptions>(options =>
            {
                options.Conventions.Add(new MediaSegmentsFilterConvention());
            });
        }
    }
}
