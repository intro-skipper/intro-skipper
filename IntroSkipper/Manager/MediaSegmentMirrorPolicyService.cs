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
    private bool _lastSeen = MediaSegmentMirrorPolicy.Enabled;

    /// <inheritdoc />
    public event EventHandler<bool>? EnabledChanged;

    /// <inheritdoc />
    public bool Enabled => MediaSegmentMirrorPolicy.Enabled;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            _lastSeen = plugin.Configuration.UpdateMediaSegments;
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
        if (_lastSeen == enabled)
        {
            return;
        }

        _lastSeen = enabled;
        EnabledChanged?.Invoke(this, enabled);
    }
}
