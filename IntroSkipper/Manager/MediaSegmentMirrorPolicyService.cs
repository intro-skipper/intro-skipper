// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;

namespace IntroSkipper.Manager;

/// <summary>
/// Default <see cref="IMediaSegmentMirrorPolicy"/>: serves <see cref="Enabled"/> as a
/// live read of <see cref="MediaSegmentMirrorPolicy"/> and raises
/// <see cref="EnabledChanged"/> from the plugin's configuration-changed event. The
/// stored last-seen value exists only for edge detection, never for serving reads.
/// </summary>
internal sealed class MediaSegmentMirrorPolicyService : IHostedService, IMediaSegmentMirrorPolicy
{
    // int for Interlocked: configuration saves arrive on arbitrary request threads,
    // and a plain read-compare-set could let two rapid opposite saves swallow the
    // enable transition entirely (the enable handler seeing stale state and
    // returning). The atomic exchange makes every observed flip raise exactly once.
    private int _lastSeen = MediaSegmentMirrorPolicy.Enabled ? 1 : 0;

    /// <inheritdoc />
    public event EventHandler<bool>? EnabledChanged;

    /// <inheritdoc />
    public bool Enabled => MediaSegmentMirrorPolicy.Enabled;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            Interlocked.Exchange(ref _lastSeen, plugin.Configuration.UpdateMediaSegments ? 1 : 0);
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        var enabled = ((Configuration.PluginConfiguration)configuration).UpdateMediaSegments;
        if (Interlocked.Exchange(ref _lastSeen, enabled ? 1 : 0) != (enabled ? 1 : 0))
        {
            EnabledChanged?.Invoke(this, enabled);
        }
    }
}
