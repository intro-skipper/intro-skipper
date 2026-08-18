// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Manager;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;

namespace IntroSkipper.SegmentChanges;

internal sealed class SegmentProjectionConfiguration : IHostedService, ISegmentProjectionConfiguration
{
    private bool _enabled = MediaSegmentMirrorPolicy.Enabled;

    /// <inheritdoc />
    public event EventHandler<bool>? EnabledChanged;

    /// <inheritdoc />
    public bool Enabled => _enabled;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            _enabled = plugin.Configuration.UpdateMediaSegments;
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
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        EnabledChanged?.Invoke(this, enabled);
    }
}
