// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Repositories;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// Background service that processes outbox entries to sync segments to Jellyfin.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="OutboxProcessorService"/> class.
/// </remarks>
/// <param name="serviceProvider">The service provider.</param>
/// <param name="logger">The logger.</param>
public class OutboxProcessorService(
    IServiceProvider serviceProvider,
    ILogger<OutboxProcessorService> logger) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger<OutboxProcessorService> _logger = logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly LibraryOptions _externalProviders = new()
    {
        DisabledMediaSegmentProviders = ["Chapter Segments Provider"]
    };

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox Processor Service started (instance: {InstanceId})", _instanceId);

        // Wait for Plugin to be initialized and database to be ready
        while ((Plugin.Instance is null || !Plugin.Instance.DatabaseReady) && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Waiting for Plugin initialization and database...");
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Double-check database is still ready
                if (Plugin.Instance?.DatabaseReady != true)
                {
                    _logger.LogDebug("Database not ready, skipping outbox processing");
                    await Task.Delay(OutboxConstants.ErrorRetryDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ProcessOutboxEntriesAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(OutboxConstants.PollingInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in outbox processor loop");
                await Task.Delay(OutboxConstants.ErrorRetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Outbox Processor Service stopped (instance: {InstanceId})", _instanceId);
    }

    private async Task ProcessOutboxEntriesAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var outboxRepository = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var mediaSegmentManager = scope.ServiceProvider.GetRequiredService<IMediaSegmentManager>();

        // Release any stale claims from crashed processors
        await outboxRepository.ReleaseStaleClaimsAsync(OutboxConstants.ClaimTimeout, cancellationToken).ConfigureAwait(false);

        // Claim pending entries atomically to prevent concurrent processing
        var claimedEntries = await outboxRepository.ClaimPendingAsync(_instanceId, OutboxConstants.BatchSize, cancellationToken).ConfigureAwait(false);

        if (claimedEntries.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Processing {Count} claimed outbox entries", claimedEntries.Count);

        // Group by ItemId to process all changes for an item at once
        var groupedEntries = claimedEntries.GroupBy(e => e.ItemId);

        foreach (var group in groupedEntries)
        {
            var itemId = group.Key;
            var entries = group.ToList();
            var entryIds = entries.Select(e => e.Id).ToList();

            try
            {
                var item = Plugin.Instance?.GetItem(itemId);
                if (item is null)
                {
                    _logger.LogWarning("Item {ItemId} not found, marking outbox entries as processed", itemId);
                    await outboxRepository.MarkProcessedBatchAsync(entryIds, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Trigger Jellyfin to refresh segments from our provider
                await mediaSegmentManager.RunSegmentPluginProviders(item, _externalProviders, true, cancellationToken).ConfigureAwait(false);

                // Mark all entries for this item as processed
                await outboxRepository.MarkProcessedBatchAsync(entryIds, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Synced {Count} segment changes for item {ItemId}", entries.Count, itemId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing outbox entries for item {ItemId}", itemId);
                await outboxRepository.IncrementRetryBatchAsync(entryIds, ex.Message, cancellationToken).ConfigureAwait(false);
            }
        }

        // Cleanup old processed entries
        try
        {
            var retentionCutoff = DateTime.UtcNow - OutboxConstants.RetentionPeriod;
            await outboxRepository.DeleteOldEntriesAsync(retentionCutoff, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up old outbox entries");
        }
    }
}
