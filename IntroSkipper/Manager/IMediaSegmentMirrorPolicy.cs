// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;

namespace IntroSkipper.Manager;

/// <summary>
/// The "Jellyfin is only a mirror" policy: every write into Jellyfin's MediaSegments
/// table is gated by the <c>UpdateMediaSegments</c> configuration flag. Reads are
/// never gated. <see cref="Enabled"/> reads the live configuration on every call, so
/// the policy keeps a single home; <see cref="EnabledChanged"/> lets the projection
/// worker replay journaled work when the flag turns on.
/// </summary>
internal interface IMediaSegmentMirrorPolicy
{
    /// <summary>Raised when the mirroring flag flips.</summary>
    event EventHandler<bool>? EnabledChanged;

    /// <summary>Gets a value indicating whether plugin segments are mirrored into Jellyfin.</summary>
    bool Enabled { get; }
}

/// <summary>
/// Default <see cref="IMediaSegmentMirrorPolicy"/> over the plugin configuration;
/// hosted so it can subscribe to configuration saves and raise
/// <see cref="EnabledChanged"/> on each flip. The stored last-seen value exists only
/// for edge detection, never for serving reads.
/// </summary>
internal sealed class MediaSegmentMirrorPolicy : IHostedService, IMediaSegmentMirrorPolicy
{
    // int for Interlocked: configuration saves arrive on arbitrary request threads,
    // and a plain read-compare-set could let two rapid opposite saves swallow the
    // enable transition entirely (the enable handler seeing stale state and
    // returning). The atomic exchange makes every observed flip raise exactly once.
    // Seeded in StartAsync, which is also where the subscription begins.
    private int _lastSeen;

    /// <inheritdoc />
    public event EventHandler<bool>? EnabledChanged;

    /// <summary>
    /// Gets a value indicating whether plugin segments are mirrored into Jellyfin.
    /// Defaults to enabled when no plugin instance is available (unit-test hosts).
    /// </summary>
    public bool Enabled => Plugin.Instance?.Configuration.UpdateMediaSegments ?? true;

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
