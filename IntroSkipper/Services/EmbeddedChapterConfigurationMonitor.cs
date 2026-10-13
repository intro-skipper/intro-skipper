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
    private readonly Func<IReadOnlyCollection<Guid>, Task> _reanalyze = scheduler.ReanalyzeItemsAsync;
    private readonly IntroSkipperDatabase _database = database;
    private readonly ILogger<EmbeddedChapterConfigurationMonitor> _logger = logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _workLock = new();

    private Task _work = Task.CompletedTask;
    private int _lastSeen;
    private long _configurationVersion;
    private bool _retryPending;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            lock (_workLock)
            {
                _lastSeen = plugin.Configuration.UseEmbeddedChaptersForAnalysis ? 1 : 0;
                _configurationVersion = 0;
                _retryPending = false;
            }

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
        lock (_workLock)
        {
            var enabled = ((PluginConfiguration)configuration).UseEmbeddedChaptersForAnalysis;
            var state = enabled ? 1 : 0;
            if (_lastSeen == state && !_retryPending)
            {
                return;
            }

            _lastSeen = state;
            _retryPending = false;
            var version = ++_configurationVersion;
            _work = _work.ContinueWith(
                _ => ReanalyzeEmbeddedItemsAsync(_stopping.Token, version, state),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task ReanalyzeEmbeddedItemsAsync(CancellationToken cancellationToken, long version, int state)
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
            await _reanalyze(items).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogConfigurationRescanFailed(_logger, ex);
            lock (_workLock)
            {
                // Keep only this transition retryable. A newer notification may already be
                // queued, so an older failure must not overwrite its observed state.
                if (_configurationVersion == version && _lastSeen == state)
                {
                    _retryPending = true;
                }
            }
        }
    }

    private async Task<IReadOnlyCollection<Guid>> FindItemsRequiringReanalysisAsync(CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<Guid, QueuedEpisode>();
        var owners = _seasonResolver.EnumerateLibrary(out var failures);
        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_seasonResolver.TryResolve(owner, includeExcluded: false, out var seasons))
            {
                failures++;
                continue;
            }

            foreach (var episode in seasons.SelectMany(season => season.Episodes))
            {
                candidates.TryAdd(episode.EpisodeId, episode);
            }
        }

        if (failures > 0)
        {
            LogIncompleteLibraryDiscovery(_logger, failures);
            throw new InvalidOperationException($"Could not resolve {failures} library entries while selecting embedded chapter reanalysis items.");
        }

        var items = (await _database.GetItemIdsBySegmentSourcesAsync(
                [SegmentSource.Chapter, SegmentSource.Combined],
                cancellationToken)
            .ConfigureAwait(false))
            .Where(candidates.ContainsKey)
            .ToHashSet();

        // Include items with Jellyfin chapters in both directions. A chapter-derived result
        // can have a non-Chapter source after another analyzer wins, so filtering only by
        // segment provenance would leave that result stale when embedded mode is enabled.
        // The source choice stays out of the config hash, so this targeted set is required.
        foreach (var episode in candidates.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!items.Contains(episode.EpisodeId)
                && Plugin.Instance?.GetChapters(episode.EpisodeId).Count > 0)
            {
                items.Add(episode.EpisodeId);
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
            try
            {
                if ((await _ffmpegService.GetEmbeddedChaptersAsync(episode, ct).ConfigureAwait(false)).Length > 0)
                {
                    embeddedItems.Add(episode.EpisodeId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogEmbeddedProbeSelectionFailed(_logger, ex, episode.Name);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not inspect embedded chapters for {Episode} while selecting the targeted reanalysis set")]
    private static partial void LogEmbeddedProbeSelectionFailed(ILogger logger, Exception exception, string episode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedded chapter reanalysis discovery was incomplete; {Failures} library entries could not be resolved")]
    private static partial void LogIncompleteLibraryDiscovery(ILogger logger, int failures);
}
