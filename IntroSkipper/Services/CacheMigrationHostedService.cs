// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// Runs cache migration once during plugin startup, then records completion in the plugin configuration.
/// </summary>
public sealed class CacheMigrationHostedService : BackgroundService
{
    private readonly ILogger<CacheMigrationHostedService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheMigrationHostedService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="fileSystem">File system.</param>
    public CacheMigrationHostedService(
        ILogger<CacheMigrationHostedService> logger,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give Jellyfin a brief moment to finish bringing up the library.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogDebug("Plugin instance not available; skipping startup cache migration");
            return;
        }

        var completed = plugin.Configuration.CacheMigrationVersion >= CacheMigration.CurrentCacheMigrationVersion;
        if (completed)
        {
            _logger.LogDebug(
                "Startup cache migration already completed (version {Version}); skipping",
                plugin.Configuration.CacheMigrationVersion);
            return;
        }

        try
        {
            _logger.LogInformation("Starting startup cache migration");

            await CacheMigration.RunAsync(
                _logger,
                _loggerFactory,
                _libraryManager,
                _providerManager,
                _fileSystem,
                stoppingToken).ConfigureAwait(false);

            plugin.Configuration.CacheMigrationVersion = CacheMigration.CurrentCacheMigrationVersion;
            plugin.SaveConfiguration();

            _logger.LogInformation("Startup cache migration finished successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Startup cache migration canceled");
        }
        catch (Exception ex)
        {
            // Do not block server startup; log and leave version untouched so it can retry on next startup.
            _logger.LogWarning(ex, "Startup cache migration failed");
        }
    }
}
