// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Threading.Channels;
using IntroSkipper.Db;
using IntroSkipper.Services;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Remote;

/// <summary>Broadcasts invalidations after saved settings; connected nodes fetch authoritative snapshots.</summary>
public sealed partial class NodeSettingsNotifier(IntroSkipperDatabase? database = null, AnalysisScheduler? scheduler = null, ILogger<NodeSettingsNotifier>? logger = null) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Channel<bool>> _listeners = new();
    private readonly Channel<bool> _reschedule = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        foreach (var channel in _listeners.Values)
        {
            channel.Writer.TryComplete();
        }

        return base.StopAsync(cancellationToken);
    }

    /// <summary>Signals a committed configuration or node policy change.</summary>
    public void Publish()
    {
        _reschedule.Writer.TryWrite(true);
        foreach (var channel in _listeners.Values)
        {
            channel.Writer.TryWrite(true);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var changed in _reschedule.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (database is null || scheduler is null)
            {
                continue;
            }

            try
            {
                var ids = await database.GetPendingRemoteItemIdsAsync(stoppingToken).ConfigureAwait(false);
                await Task.WhenAll(ids.Select(scheduler.ItemChangedAsync)).WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (logger is not null)
                {
                    LogRescheduleFailed(logger, exception);
                }
            }
        }
    }

    internal async IAsyncEnumerable<bool> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        _listeners[id] = channel;
        channel.Writer.TryWrite(true);
        try
        {
            await foreach (var value in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                yield return value;
            }
        }
        finally
        {
            _listeners.TryRemove(id, out _);
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration) => Publish();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not reschedule pending node work after settings changed; the next scheduled or manual scan will retry.")]
    private static partial void LogRescheduleFailed(ILogger logger, Exception exception);
}
