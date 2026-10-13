// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// Reopens only media whose chapter-derived analysis can change when the chapter source setting
/// changes. The targeted queue pass resets analysis records but retains the detection cache,
/// because changing the chapter source does not change any media-derived fingerprint.
/// </summary>
internal sealed partial class EmbeddedChapterConfigurationMonitor(
    SeasonResolver seasonResolver,
    IFFmpegService ffmpegService,
    AnalysisScheduler scheduler,
    IntroSkipperDatabase database,
    ILogger<EmbeddedChapterConfigurationMonitor> logger) : IHostedService, IDisposable
{
    private readonly SeasonResolver _seasonResolver = seasonResolver;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly AnalysisScheduler _scheduler = scheduler;
    private readonly IntroSkipperDatabase _database = database;
    private readonly ILogger<EmbeddedChapterConfigurationMonitor> _logger = logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _workLock = new();

    private Task _work = Task.CompletedTask;
    private int _lastSeen;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            Interlocked.Exchange(ref _lastSeen, plugin.Configuration.UseEmbeddedChaptersForAnalysis ? 1 : 0);
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        Task work;
        lock (_workLock)
        {
            work = _work;
        }

        await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        var enabled = ((PluginConfiguration)configuration).UseEmbeddedChaptersForAnalysis;
        if (Interlocked.Exchange(ref _lastSeen, enabled ? 1 : 0) == (enabled ? 1 : 0))
        {
            return;
        }

        lock (_workLock)
        {
            _work = _work.ContinueWith(
                _ => ReanalyzeEmbeddedItemsAsync(_stopping.Token),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task ReanalyzeEmbeddedItemsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var items = await FindItemsRequiringReanalysisAsync(cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                LogNoEmbeddedItems(_logger);
                return;
            }

            LogQueueingItems(_logger, items.Count);
            await _scheduler.ReanalyzeItemsAsync(items).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogConfigurationRescanFailed(_logger, ex);
        }
    }

    private async Task<IReadOnlyCollection<Guid>> FindItemsRequiringReanalysisAsync(CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<Guid, QueuedEpisode>();
        foreach (var owner in _seasonResolver.EnumerateLibrary(out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_seasonResolver.TryResolve(owner, includeExcluded: false, out var seasons))
            {
                continue;
            }

            foreach (var episode in seasons.SelectMany(season => season.Episodes))
            {
                candidates.TryAdd(episode.EpisodeId, episode);
            }
        }

        var items = (await _database.GetItemIdsBySegmentSourcesAsync(
                [SegmentSource.Chapter, SegmentSource.Combined],
                cancellationToken)
            .ConfigureAwait(false))
            .Where(candidates.ContainsKey)
            .ToHashSet();

        // When returning to Jellyfin's chapter table, include items whose current table
        // has chapters even if the embedded pass produced no chapter segment for them.
        // This keeps the disabled state equivalent to the historical behavior without
        // hashing the source choice into every analysis record.
        if (!(Plugin.Instance?.Configuration.UseEmbeddedChaptersForAnalysis ?? false))
        {
            foreach (var episode in candidates.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!items.Contains(episode.EpisodeId)
                    && Plugin.Instance?.GetChapters(episode.EpisodeId).Count > 0)
                {
                    items.Add(episode.EpisodeId);
                }
            }
        }

        var embeddedItems = new ConcurrentBag<Guid>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Plugin.Instance?.Configuration.MaxParallelism ?? 1),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(candidates.Values, options, async (episode, ct) =>
        {
            if ((await _ffmpegService.GetEmbeddedChaptersAsync(episode, ct).ConfigureAwait(false)).Length > 0)
            {
                embeddedItems.Add(episode.EpisodeId);
            }
        }).ConfigureAwait(false);

        items.UnionWith(embeddedItems);
        return items;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedded chapter setting changed, but no eligible media with embedded chapters was found")]
    private static partial void LogNoEmbeddedItems(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queueing targeted reanalysis for {Count} media items affected by the chapter source change")]
    private static partial void LogQueueingItems(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to queue reanalysis after the embedded chapter setting changed")]
    private static partial void LogConfigurationRescanFailed(ILogger logger, Exception exception);
}
