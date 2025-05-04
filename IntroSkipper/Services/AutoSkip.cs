// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using IntroSkipper.Configuration;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// Automatically skip past introduction sequences.
/// Commands clients to seek to the end of the intro as soon as they start playing it.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="AutoSkip"/> class.
/// </remarks>
/// <param name="userDataManager">User data manager.</param>
/// <param name="sessionManager">Session manager.</param>
/// <param name="logger">Logger.</param>
public sealed class AutoSkip(
    IUserDataManager userDataManager,
    ISessionManager sessionManager,
    ILogger<AutoSkip> logger) : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager = userDataManager;
    private readonly ISessionManager _sessionManager = sessionManager;
    private readonly ILogger<AutoSkip> _logger = logger;
    private readonly System.Timers.Timer _playbackTimer = new(1000);
    private readonly ConcurrentDictionary<string, List<Segment>> _sentSeekCommand = [];
    private PluginConfiguration _config = new();
    private HashSet<string> _clientList = [];
    private HashSet<AnalysisMode> _segmentTypes = [];
    private bool _autoSkipEnabled;

    private void AutoSkipChanged(object? sender, BasePluginConfiguration e)
    {
        _config = (PluginConfiguration)e;
        _clientList = [.. _config.ClientList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        _segmentTypes = [.. _config.TypeList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Enum.Parse<AnalysisMode>)];
        _autoSkipEnabled = (_config.AutoSkip || _clientList.Count > 0) && _segmentTypes.Count > 0;
        _logger.LogDebug("Setting playback timer enabled to {AutoSkipEnabled}", _autoSkipEnabled);
        _playbackTimer.Enabled = _autoSkipEnabled;
    }

    private void UserDataManager_UserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // Ignore all events except playback start & end
        if (e.SaveReason is not (UserDataSaveReason.PlaybackStart or UserDataSaveReason.PlaybackFinished) || !_autoSkipEnabled)
        {
            return;
        }

        var itemId = e.Item.Id;

        SessionInfo? session = null;

        try
        {
            var userSessions = _sessionManager.Sessions
                .Where(s => s.UserId == e.UserId).ToList();

            _logger.LogDebug("Found {Count} sessions for user {UserId}", userSessions.Count, e.UserId);

            session = userSessions
                .FirstOrDefault(s => s.NowPlayingItem?.Id == itemId);

            if (session is null)
            {
                _logger.LogInformation("Unable to find active session for user {UserId} and item {ItemId}", e.UserId, itemId);

                // Clean up orphaned sessions
                var orphaned = userSessions
                    .Where(s => s.NowPlayingItem is null)
                    .Where(s => _sentSeekCommand.TryRemove(s.DeviceId, out _))
                    .ToList();

                if (orphaned.Count > 0)
                {
                    _logger.LogDebug("Removed {Count} orphaned sessions for devices: {Devices}", orphaned.Count, string.Join(", ", orphaned.Select(s => s.DeviceId)));
                }

                return;
            }
        }
        catch (ResourceNotFoundException ex)
        {
            _logger.LogWarning(ex, "Resource not found while processing session");
            return;
        }

        _logger.LogInformation("Found active session {SessionId} for user {UserId} and item {ItemId}", session.Id, e.UserId, itemId);

        // Reset the seek command state for this device.
        var device = session.DeviceId;
        _logger.LogDebug("Getting intros for session {Session}", device);

        bool firstEpisode = e.Item is Episode episode && _config.SkipFirstEpisode && episode.IndexNumber == 1;
        var intros = Plugin.Instance!.GetTimestamps(itemId)
                .Where(i => _segmentTypes.Contains(i.Key) && (!firstEpisode || i.Key != AnalysisMode.Introduction))
                .Select(i => i.Value)
                .ToList();

        _sentSeekCommand.AddOrUpdate(device, intros, (_, _) => intros);
    }

    /// <summary>
    /// Format the notification text for the user.
    /// </summary>
    /// <param name="template">The template to format.</param>
    /// <param name="segmentType">The type of segment being skipped.</param>
    /// <param name="start">The start time of the segment.</param>
    /// <param name="end">The end time of the segment.</param>
    /// <returns>The formatted notification text.</returns>
    public static string FormatNotificationText(string? template, AnalysisMode segmentType, double start, double end)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        var duration = end - start;
        return template
            .Replace("%segmenttype", segmentType.ToString(), StringComparison.Ordinal)
            .Replace("%start", $"{start:F0}", StringComparison.Ordinal)
            .Replace("%end", $"{end:F0}", StringComparison.Ordinal)
            .Replace("%duration", $"{duration:F0}", StringComparison.Ordinal);
    }

    private bool IsSegmentPlayingAt(KeyValuePair<AnalysisMode, Intro> segment, double position)
    {
        return position >= Math.Max(1, segment.Value.IntroStart) &&
               position < segment.Value.IntroEnd - 3.0;
    }

    private bool IsAdjacentSegment(KeyValuePair<AnalysisMode, Intro> segment, double introEnd)
    {
        return introEnd + _config.MaximumTimeSkip >= segment.Value.IntroStart &&
               introEnd < segment.Value.IntroEnd;
    }

    private void PlaybackTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        foreach (var session in _sessionManager.Sessions.Where(s => _config.AutoSkip || _clientList.Contains(s.Client, StringComparer.OrdinalIgnoreCase)))
        {
            var deviceId = session.DeviceId;

            // Don't send the seek command more than once in the same session.
            if (!_sentSeekCommand.TryGetValue(deviceId, out var intros))
            {
                continue;
            }

            var position = session.PlayState.PositionTicks / TimeSpan.TicksPerSecond;

            var currentIntro = intros.FirstOrDefault(i =>
                position >= Math.Max(1, i.Start + _config.SecondsOfIntroStartToPlay) &&
                position < i.End - 3.0); // 3 seconds before the end of the intro

            if (currentIntro is null)
            {
                continue;
            }

            var introEnd = currentIntro.End;

            intros.Remove(currentIntro);

            // Check if adjacent segment is within the maximum skip range.
            var maxTimeSkip = _config.MaximumTimeSkip;
            var nextIntro = intros.FirstOrDefault(i => introEnd + maxTimeSkip >= i.Start &&
                    introEnd < i.End);

            if (nextIntro is not null)
            {
                introEnd = nextIntro.End;
                intros.Remove(nextIntro);
            }

            _logger.LogDebug("Found segment for session {Session}, removing from list, {Intros} segments remaining", deviceId, intros.Count);

            _logger.LogTrace(
                "Playback position is {Position}",
                position);

            // Notify the user that an introduction is being skipped for them.
            var notificationText = _config.AutoSkipNotificationText;

            if (!string.IsNullOrWhiteSpace(notificationText))
            {
                _sessionManager.SendMessageCommand(
                session.Id,
                session.Id,
                new MessageCommand
                {
                    Header = string.Empty,      // some clients require header to be a string instead of null
                    Text = notificationText,
                    TimeoutMs = 2000,
                },
                CancellationToken.None);
            }

            _logger.LogDebug("Sending seek command to {Session}", deviceId);

            _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest
                {
                    Command = PlaystateCommand.Seek,
                    ControllingUserId = session.UserId.ToString(),
                    SeekPositionTicks = (long)introEnd * TimeSpan.TicksPerSecond,
                },
                CancellationToken.None);

            // Flag that we've sent the seek command so that it's not sent repeatedly
            _logger.LogTrace("Setting seek command state for session {Session}", deviceId);
        }
    }

    /// <summary>
    /// Dispose resources.
    /// </summary>
    public void Dispose()
    {
        _playbackTimer.Dispose();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Setting up automatic skipping");

        _userDataManager.UserDataSaved += UserDataManager_UserDataSaved;
        Plugin.Instance!.ConfigurationChanged += AutoSkipChanged;

        // Make the timer restart automatically and set enabled to match the configuration value.
        _playbackTimer.AutoReset = true;
        _playbackTimer.Elapsed += PlaybackTimer_Elapsed;

        AutoSkipChanged(null, Plugin.Instance.Configuration);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= UserDataManager_UserDataSaved;
        Plugin.Instance!.ConfigurationChanged -= AutoSkipChanged;
        _playbackTimer.Stop();
        return Task.CompletedTask;
    }
}
