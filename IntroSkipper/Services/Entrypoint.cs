// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using IntroSkipper.Configuration;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace IntroSkipper.Services;

/// <summary>
/// Library watcher: hands changed episodes and movies to the analysis queue, which
/// resolves their seasons and analyzes them after its quiet period, and deletes the
/// fingerprint cache of removed items. Also registers the web injector at startup.
/// </summary>
/// <param name="libraryManager">Library manager.</param>
/// <param name="cacheDatabase">Detection cache database facade.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="logger">Logger.</param>
/// <param name="queue">Analysis queue the changed items are handed to.</param>
public sealed partial class Entrypoint(
    ILibraryManager libraryManager,
    DetectionCacheDatabase cacheDatabase,
    IFFmpegService ffmpegService,
    ILogger<Entrypoint> logger,
    AnalysisScheduler queue) : IHostedService
{
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly DetectionCacheDatabase _cacheDatabase = cacheDatabase;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly ILogger<Entrypoint> _logger = logger;
    private readonly AnalysisScheduler _queue = queue;

    // Jellyfin replaces the configuration object on save, so the live instance is read on
    // every use instead of a constructor-time snapshot.
    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;

        await _ffmpegService.CheckFFmpegVersionAsync(cancellationToken).ConfigureAwait(false);

        // Initialize web injector for skip button timeout modification
        if (Config.FileTransformationPluginEnabled)
        {
            InitializeWebInjector();
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Initializes the web injector for skip button timeout modification.
    /// </summary>
    private void InitializeWebInjector()
    {
        JObject payload = new JObject
        {
            { "id", "c83d86bb-a1e0-4c35-a113-e2101cf4ee6b" },
            { "fileNamePattern", "main.jellyfin.bundle.js" },
            { "callbackAssembly", GetType().Assembly.FullName },
            { "callbackClass", typeof(Injector).FullName },
            { "callbackMethod", nameof(Injector.FileTransformer) }
        };

        Assembly? fileTransformationAssembly =
            AssemblyLoadContext.All.SelectMany(x => x.Assemblies).FirstOrDefault(x =>
                x.GetName().Name == "Jellyfin.Plugin.FileTransformation");

        if (fileTransformationAssembly is null)
        {
            return;
        }

        // Untyped call into another plugin: any reflection or plugin failure here would abort Jellyfin startup.
        try
        {
            fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")
                ?.GetMethod("RegisterTransformation")
                ?.Invoke(null, [payload]);
        }
        catch (Exception ex)
        {
            LogFileTransformationRegistrationFailed(ex);
        }
    }

    /// <summary>
    /// Library item was added or updated.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private void OnItemChanged(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        if (itemChangeEventArgs.UpdateReason == ItemUpdateType.ImageUpdate)
        {
            return;
        }

        if (!TryGetValidItemForAutoProcessing(itemChangeEventArgs, out var item))
        {
            return;
        }

        // Queues the item's own id. The pass resolves the season it belongs to when it
        // starts, off this library event thread and after Jellyfin has attached a new
        // episode to its season. A replaced file needs no special handling: Jellyfin's
        // refresh has already had SegmentProvider.CleanupExtractedData erase the item's
        // records and detection cache, so the pass analyzes it like a new one. A change
        // the refresh misses is caught by queue verification comparing the stored file
        // version. The handle is dropped; the queue logs a failed pass itself.
        LogMediaLibraryChanged();
        _ = _queue.ItemChangedAsync(item.Id);
    }

    /// <summary>
    /// Library item was removed.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private void OnItemRemoved(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        if (!TryGetValidItemForAutoProcessing(itemChangeEventArgs, out var item) || item.Id == Guid.Empty)
        {
            return;
        }

        LogMediaItemRemoved(item.Id);
        // Best-effort: the facade logs and swallows database errors.
        _cacheDatabase.DeleteForItem(item.Id);
    }

    // An episode or movie with a real location, while automatic analysis is on.
    private static bool TryGetValidItemForAutoProcessing(
        ItemChangeEventArgs itemChangeEventArgs,
        [NotNullWhen(true)] out BaseItem? item)
    {
        item = null;
        if (!Config.AutoDetectIntros)
        {
            return false;
        }

        var candidate = itemChangeEventArgs.Item;
        if (!MediaItemHelper.IsSupported(candidate) || candidate.LocationType == LocationType.Virtual)
        {
            return false;
        }

        item = candidate;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Media item removed, deleting fingerprint cache for {Id}")]
    private partial void LogMediaItemRemoved(Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Media Library changed, analysis will start soon!")]
    private partial void LogMediaLibraryChanged();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not register the skip button transformation with File Transformation")]
    private partial void LogFileTransformationRegistrationFailed(Exception ex);
}
