// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Classifies one season's queued episodes against the stored analysis state: an
/// analysis record under the current configuration hash settles an episode for the
/// mode (<see cref="EpisodeState.Analyzed"/> with segments,
/// <see cref="EpisodeState.NoSegments"/> without), user segments always settle it, and
/// anything else stays <see cref="EpisodeState.NotAnalyzed"/>. The expected hash depends
/// on the season's analyzer action and the mode, not on the episode, so every per-mode
/// value is computed once per instance.
/// </summary>
internal sealed partial class QueueVerifier
{
    private readonly PluginConfiguration _config;
    private readonly IReadOnlyCollection<AnalysisMode> _modes;
    private readonly SeasonQueueSnapshot _snapshot;
    private readonly bool _ffmpegValid;
    private readonly Dictionary<AnalysisMode, AnalyzerAction> _actionByMode;
    private readonly Dictionary<AnalysisMode, string> _expectedHashByMode;

    // The hash the same configuration produces with Chromaprint available; only
    // consulted while the probe failed, see Classify.
    private readonly Dictionary<AnalysisMode, string>? _availableHashByMode;

    // First stored hash seen per mode, replaced by the first mismatching one, so the
    // reason log can quote the hash that caused the reprocessing.
    private readonly Dictionary<AnalysisMode, (string Stored, bool Mismatch)> _storedHashByMode = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueVerifier"/> class.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="modes">Analysis modes of the run.</param>
    /// <param name="snapshot">The season's stored analysis state.</param>
    /// <param name="ffmpegValid">Whether the Chromaprint capability probe succeeded.</param>
    public QueueVerifier(PluginConfiguration config, IReadOnlyCollection<AnalysisMode> modes, SeasonQueueSnapshot snapshot, bool ffmpegValid)
    {
        _config = config;
        _modes = modes;
        _snapshot = snapshot;
        _ffmpegValid = ffmpegValid;
        _actionByMode = new Dictionary<AnalysisMode, AnalyzerAction>(modes.Count);
        _expectedHashByMode = new Dictionary<AnalysisMode, string>(modes.Count);
        _availableHashByMode = ffmpegValid ? null : new Dictionary<AnalysisMode, string>(modes.Count);
        foreach (var mode in modes)
        {
            var action = snapshot.AnalyzerActionByMode.TryGetValue(mode, out var savedAction) ? savedAction : AnalyzerAction.Default;
            _actionByMode[mode] = action;
            _expectedHashByMode[mode] = ConfigHasher.Analysis(config, mode, action, ffmpegValid);
            _availableHashByMode?.Add(mode, ConfigHasher.Analysis(config, mode, action, ffmpegValid: true));
        }
    }

    /// <summary>
    /// Sets the candidate's per-mode analysis state from the season snapshot.
    /// </summary>
    /// <param name="candidate">A queued episode that exists on disk and is not excluded.</param>
    public void Classify(QueuedEpisode candidate)
    {
        foreach (var mode in _modes)
        {
            // An empty hash is equivalent to no durable analysis state. It can be present on
            // rows created before hashing was recorded and must not settle an item forever.
            var hasAnalyzedHash = _snapshot.AnalyzedConfigHashes.TryGetValue((candidate.EpisodeId, mode), out var analyzedHash)
                && !string.IsNullOrEmpty(analyzedHash);
            var hashMatches = hasAnalyzedHash && string.Equals(analyzedHash, _expectedHashByMode[mode], StringComparison.Ordinal);

            // A failed FFmpeg capability probe must not invalidate good Chromaprint results.
            // Availability is an upward invalidation: a later successful probe can reopen a
            // season that was settled without Chromaprint, but a transient failed probe cannot
            // discard results produced while it was available.
            if (!hashMatches && hasAnalyzedHash && _availableHashByMode is { } availableHashByMode)
            {
                hashMatches = string.Equals(analyzedHash, availableHashByMode[mode], StringComparison.Ordinal);
            }

            if (hasAnalyzedHash)
            {
                var mismatch = !hashMatches;
                if (!_storedHashByMode.TryGetValue(mode, out var stored) || (mismatch && !stored.Mismatch))
                {
                    _storedHashByMode[mode] = (analyzedHash!, mismatch);
                }
            }

            if (_snapshot.SegmentModesByEpisodeId.TryGetValue(candidate.EpisodeId, out var modesWithSegments) &&
                modesWithSegments.Contains(mode))
            {
                var isUserProvided = _snapshot.UserProvidedByMode.TryGetValue(mode, out var userProvided) &&
                                     userProvided.Contains(candidate.EpisodeId);

                // Always preserve user-provided segments. Automatic results are reusable only
                // when the stored per-item hash still describes the current configuration.
                if (isUserProvided || hashMatches)
                {
                    candidate.SetAnalyzed(mode, isUserProvided ? EpisodeState.UserProvided : EpisodeState.Analyzed);
                }
            }
            else if (hashMatches)
            {
                candidate.SetAnalyzed(mode, EpisodeState.NoSegments);
            }
        }
    }

    /// <summary>
    /// Logs why a verified season still contains pending work. Hash changes are information-level
    /// events because they explain unexpected reprocessing; normal first scans and newly added items
    /// remain debug-level noise.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="verified">The classified episodes of the season.</param>
    public void LogAnalysisReasons(ILogger logger, IReadOnlyList<QueuedEpisode> verified)
    {
        if (verified.Count == 0 || AnalysisEligibility.IsSeasonZeroOptedOut(verified[0], _config))
        {
            return;
        }

        var first = verified[0];
        foreach (var mode in _modes)
        {
            if (_actionByMode[mode] == AnalyzerAction.None)
            {
                continue;
            }

            var pending = verified.Count(episode => episode.NeedsAnalysis(mode));
            if (pending == 0 || !verified.Any(episode => episode.GetAnalyzed(mode) == EpisodeState.NotAnalyzed))
            {
                continue;
            }

            if (_storedHashByMode.TryGetValue(mode, out var stored) && stored.Mismatch)
            {
                LogSeasonConfigHashChanged(
                    logger,
                    mode,
                    pending,
                    verified.Count,
                    first.SeriesName,
                    first.SeasonNumber,
                    stored.Stored,
                    _expectedHashByMode[mode],
                    ChromaprintAffectsMode(mode) ? _ffmpegValid.ToString() : "n/a");
            }
            else
            {
                LogSeasonQueuedForAnalysis(
                    logger,
                    mode,
                    pending,
                    verified.Count,
                    first.SeriesName,
                    first.SeasonNumber,
                    _storedHashByMode.ContainsKey(mode) ? AnalysisReason.NotRecorded : AnalysisReason.NoStoredState);
            }
        }
    }

    private static bool ChromaprintAffectsMode(AnalysisMode mode)
        => mode is AnalysisMode.Introduction or AnalysisMode.Credits or AnalysisMode.Recap;

    [LoggerMessage(Level = LogLevel.Debug, Message = "[Mode: {Mode}] Queuing {Count} of {Total} items in {Name} season {Season} for analysis: {Reason}")]
    private static partial void LogSeasonQueuedForAnalysis(ILogger logger, AnalysisMode mode, int count, int total, string name, int season, AnalysisReason reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Queuing {Count} of {Total} items in {Name} season {Season} for analysis: analysis configuration hash changed from \"{StoredHash}\" to \"{ExpectedHash}\" (chromaprint available: {ChromaprintAvailable})")]
    private static partial void LogSeasonConfigHashChanged(ILogger logger, AnalysisMode mode, int count, int total, string name, int season, string storedHash, string expectedHash, string chromaprintAvailable);
}
