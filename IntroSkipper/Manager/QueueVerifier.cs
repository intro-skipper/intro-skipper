// SPDX-FileCopyrightText: 2026 rlauuzo
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
/// anything else stays <see cref="EpisodeState.NotAnalyzed"/>. A record whose file
/// version differs from the episode's current one no longer describes the file. A Recap or
/// Preview record, whose version can also cover the text subtitle sidecars, then reopens
/// only its mode unless it proves the media file was replaced; any other record flags the
/// episode <see cref="QueuedEpisode.FileChanged"/>, and it stays open. The expected hash depends
/// on the season's analyzer action and the mode, not on the episode, so it is computed once
/// per instance for every mode, including those the pass does not run: the file-version
/// check judges their records too.
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

    // Pending episodes per mode whose record still matches the configuration but not the
    // version the mode records, for the reason log.
    private readonly Dictionary<AnalysisMode, int> _reopenedByVersionByMode = [];

    // Episodes whose records predate file versioning, with the version to stamp on them.
    private readonly Dictionary<Guid, long> _fileVersionBackfill = [];

    private static readonly AnalysisMode[] AllModes = Enum.GetValues<AnalysisMode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueVerifier"/> class.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="modes">Analysis modes of the pass.</param>
    /// <param name="snapshot">The season's stored analysis state.</param>
    /// <param name="ffmpegValid">Whether the Chromaprint capability probe succeeded.</param>
    /// <param name="analysisPercentOverride">Optional season-level percentage override.</param>
    /// <param name="analysisLengthLimitOverride">Optional season-level runtime limit override in minutes.</param>
    /// <param name="previewFromCreditsEndOverride">Optional season-level setting for deriving a Preview segment from Credits.</param>
    public QueueVerifier(
        PluginConfiguration config,
        IReadOnlyCollection<AnalysisMode> modes,
        SeasonQueueSnapshot snapshot,
        bool ffmpegValid,
        int? analysisPercentOverride = null,
        int? analysisLengthLimitOverride = null,
        bool? previewFromCreditsEndOverride = null)
    {
        _config = config;
        _modes = modes;
        _snapshot = snapshot;
        _ffmpegValid = ffmpegValid;
        _actionByMode = new Dictionary<AnalysisMode, AnalyzerAction>(AllModes.Length);
        _expectedHashByMode = new Dictionary<AnalysisMode, string>(AllModes.Length);
        _availableHashByMode = ffmpegValid ? null : new Dictionary<AnalysisMode, string>(AllModes.Length);

        // Every mode, not only the pass's: ClassifyFileVersion judges the records of the
        // modes the pass does not run by their hash too.
        foreach (var mode in AllModes)
        {
            var action = snapshot.AnalyzerActionByMode.TryGetValue(mode, out var savedAction) ? savedAction : AnalyzerAction.Default;
            _actionByMode[mode] = action;
            _expectedHashByMode[mode] = ConfigHasher.Analysis(config, mode, action, ffmpegValid, analysisPercentOverride, analysisLengthLimitOverride, previewFromCreditsEndOverride);
            _availableHashByMode?.Add(mode, ConfigHasher.Analysis(
                config,
                mode,
                action,
                ffmpegValid: true,
                analysisPercentOverride: analysisPercentOverride,
                analysisLengthLimitOverride: analysisLengthLimitOverride,
                previewFromCreditsEndOverride: previewFromCreditsEndOverride));
        }
    }

    /// <summary>
    /// Gets the episodes classified so far whose records carry no file version, each with
    /// the version to stamp on them, for <c>BackfillFileVersionsAsync</c>.
    /// </summary>
    public IReadOnlyDictionary<Guid, long> FileVersionBackfill => _fileVersionBackfill;

    /// <summary>
    /// Sets the candidate's per-mode analysis state from the season snapshot.
    /// </summary>
    /// <param name="candidate">A queued episode that exists on disk and is not excluded.</param>
    public void Classify(QueuedEpisode candidate)
    {
        var (fileChanged, reopenedModes) = ClassifyFileVersion(candidate);
        foreach (var mode in _modes)
        {
            // An empty hash is equivalent to no durable analysis state. It can be present on
            // rows created before hashing was recorded and must not settle an item forever.
            var hasAnalyzedHash = _snapshot.AnalysisRecords.TryGetValue((candidate.EpisodeId, mode), out var record)
                && !string.IsNullOrEmpty(record.ConfigHash);
            var hashMatches = hasAnalyzedHash && HashMatches(record, mode);

            // A replaced media file voids the record, so its hash explains nothing.
            if (hasAnalyzedHash && !fileChanged)
            {
                var mismatch = !hashMatches;
                if (!_storedHashByMode.TryGetValue(mode, out var stored) || (mismatch && !stored.Mismatch))
                {
                    _storedHashByMode[mode] = (record.ConfigHash, mismatch);
                }
            }

            // A record made for a different version of the file, or of the text sidecars a
            // subtitle-detecting mode reads, settles nothing, whatever its hash says. One whose
            // hash matches was reopened by its version alone, and the reason log says so.
            var reopenedByVersion = !fileChanged && hashMatches && reopenedModes.Contains(mode);
            if (fileChanged || reopenedModes.Contains(mode))
            {
                hashMatches = false;
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

            if (reopenedByVersion && candidate.NeedsAnalysis(mode))
            {
                _reopenedByVersionByMode[mode] = _reopenedByVersionByMode.GetValueOrDefault(mode) + 1;
            }
        }
    }

    /// <summary>
    /// Compares the episode's file version with every record the item has, whatever the
    /// record's mode: a record of a disabled mode still proves the file changed, and its
    /// stale segments still have to go.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>A record without a version predates versioning and is stamped with
    /// the media version seen now, unless the file changed, in which case the reset rewrites
    /// it.</description></item>
    /// <item><description>A Recap or Preview record must carry the version its mode records
    /// now: <see cref="QueuedEpisode.SubtitleFileVersion"/> while the mode's subtitle detection
    /// is active, the media version otherwise. A mismatch reopens only that mode. It can come
    /// from a rewritten sidecar, which changes what subtitle detection reads but not the media
    /// the other modes and the fingerprints describe, or from a record written before subtitle
    /// detection was turned on or off. One exception: with the mode's subtitle detection off,
    /// a record whose hash still matches was written under today's settings, so its version can
    /// only differ because the media file was replaced, and the episode is flagged
    /// <see cref="QueuedEpisode.FileChanged"/>. That holds whether or not the pass runs the
    /// mode.</description></item>
    /// <item><description>Any other record must carry the media version, or the episode is
    /// flagged <see cref="QueuedEpisode.FileChanged"/>.</description></item>
    /// </list>
    /// A version the episode does not have cannot be compared: without a media version
    /// (Jellyfin holds no write time) a record keeps its verdict and nothing is stamped,
    /// except a Recap or Preview record whose mode records the sidecar version. That record
    /// still compares the sidecar version, and reopens its mode when the version is gone
    /// because the last sidecar was removed.
    /// </remarks>
    /// <returns>Whether a record shows the media file was replaced, and the Recap and Preview
    /// modes whose records carry a version other than the one they record now.</returns>
    private (bool FileChanged, IReadOnlySet<AnalysisMode> ReopenedModes) ClassifyFileVersion(QueuedEpisode candidate)
    {
        HashSet<AnalysisMode> reopened = [];
        var fileVersion = candidate.FileVersion;
        var needsBackfill = false;
        foreach (var mode in AllModes)
        {
            if (!_snapshot.AnalysisRecords.TryGetValue((candidate.EpisodeId, mode), out var record))
            {
                continue;
            }

            if (record.FileVersion is not { } recorded)
            {
                needsBackfill = true;
            }
            else if (mode is AnalysisMode.Recap or AnalysisMode.Preview)
            {
                var current = _config.RecordedFileVersion(candidate, mode);
                if (recorded == current)
                {
                    continue;
                }

                if (_config.ActiveSubtitlePattern(mode) is null)
                {
                    if (current is null)
                    {
                        continue;
                    }

                    if (HashMatches(record, mode))
                    {
                        candidate.FileChanged = true;
                        return (true, reopened);
                    }
                }

                reopened.Add(mode);
            }
            else if (fileVersion is { } media && recorded != media)
            {
                candidate.FileChanged = true;
                return (true, reopened);
            }
        }

        if (needsBackfill && fileVersion is { } version)
        {
            _fileVersionBackfill.TryAdd(candidate.EpisodeId, version);
        }

        return (false, reopened);
    }

    /// <summary>
    /// Whether the record's hash describes the current configuration of the mode, under the
    /// season's saved analyzer action. Answers for every mode, including those the pass does
    /// not run.
    /// </summary>
    /// <remarks>
    /// A failed FFmpeg capability probe must not invalidate good Chromaprint results.
    /// Availability is an upward invalidation: a later successful probe can reopen a season
    /// that was settled without Chromaprint, but a transient failed probe cannot discard
    /// results produced while it was available.
    /// </remarks>
    private bool HashMatches(AnalysisRecord record, AnalysisMode mode)
        => string.Equals(record.ConfigHash, _expectedHashByMode[mode], StringComparison.Ordinal)
            || (_availableHashByMode is { } availableHashByMode
                && string.Equals(record.ConfigHash, availableHashByMode[mode], StringComparison.Ordinal));

    /// <summary>
    /// Logs why a verified season still contains pending work. Hash and file changes are
    /// information-level events because they explain unexpected reprocessing; normal first scans
    /// and newly added items remain debug-level noise. A Recap or Preview mode that only its
    /// file version reopened logs that the files changed.
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
        var changedFiles = verified.Count(episode => episode.FileChanged);
        if (changedFiles > 0)
        {
            LogSeasonFilesChanged(logger, changedFiles, verified.Count, first.SeriesName, first.SeasonNumber);
        }

        foreach (var mode in _modes)
        {
            if (_actionByMode[mode] == AnalyzerAction.None)
            {
                continue;
            }

            // Changed files were reported above; the per-mode reasons cover the rest.
            var pending = verified.Count(episode => !episode.FileChanged && episode.NeedsAnalysis(mode));
            if (pending == 0 || !verified.Any(episode => !episode.FileChanged && episode.GetAnalyzed(mode) == EpisodeState.NotAnalyzed))
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
            else if (_reopenedByVersionByMode.TryGetValue(mode, out var reopened))
            {
                LogSeasonModeFilesChanged(logger, mode, reopened, verified.Count, first.SeriesName, first.SeasonNumber);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-analyzing {Count} of {Total} items in {Name} season {Season}: media file changed since analysis")]
    private static partial void LogSeasonFilesChanged(ILogger logger, int count, int total, string name, int season);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Re-analyzing {Count} of {Total} items in {Name} season {Season}: media or subtitle files changed since analysis")]
    private static partial void LogSeasonModeFilesChanged(ILogger logger, AnalysisMode mode, int count, int total, string name, int season);
}
