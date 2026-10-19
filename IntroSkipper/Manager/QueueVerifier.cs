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
/// version differs from the episode's current one no longer describes the file: the
/// episode is flagged <see cref="QueuedEpisode.FileChanged"/> and stays open. A record
/// under the hash the current settings produce for the other chapter source settles the
/// episode too, flagged for the season pass's chapter source check
/// (<see cref="QueuedEpisode.ChapterSourceChecks"/>), which reopens the mode only when the two
/// sources hold different chapters. The expected hash depends on the season's analyzer action
/// and the mode, not on the episode, so every per-mode value is computed once per instance.
/// </summary>
internal sealed partial class QueueVerifier
{
    private readonly PluginConfiguration _config;
    private readonly IReadOnlyCollection<AnalysisMode> _modes;
    private readonly SeasonQueueSnapshot _snapshot;
    private readonly bool _ffmpegValid;
    private readonly Dictionary<AnalysisMode, AnalyzerAction> _actionByMode;
    private readonly Dictionary<AnalysisMode, string> _expectedHashByMode;
    private readonly Dictionary<AnalysisMode, string> _subtitleIndependentHashByMode = [];
    private readonly Dictionary<AnalysisMode, string>? _availableSubtitleIndependentHashByMode;

    // The hash the same configuration produces with Chromaprint available; only
    // consulted while the probe failed, see Classify.
    private readonly Dictionary<AnalysisMode, string>? _availableHashByMode;

    // The hashes the same configuration produces under the other chapter source, so a record
    // written from that source is told from one written under other settings. Only for modes
    // whose action runs an analyzer: a None action re-records the episode at no cost.
    private readonly Dictionary<AnalysisMode, string> _otherSourceHashByMode = [];
    private readonly Dictionary<AnalysisMode, string>? _availableOtherSourceHashByMode;

    // First stored hash seen per mode, replaced by the first mismatching one, so the
    // reason log can quote the hash that caused the reprocessing.
    private readonly Dictionary<AnalysisMode, (string Stored, bool Mismatch)> _storedHashByMode = [];

    // Episodes whose records predate file versioning, with the version to stamp on them.
    private readonly Dictionary<Guid, long> _fileVersionBackfill = [];

    private static readonly AnalysisMode[] AllModes = Enum.GetValues<AnalysisMode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueVerifier"/> class.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="modes">Analysis modes of the run.</param>
    /// <param name="snapshot">The season's stored analysis state.</param>
    /// <param name="ffmpegValid">Whether the Chromaprint capability probe succeeded.</param>
    /// <param name="analysisPercentOverride">Optional season-level percentage override.</param>
    /// <param name="analysisLengthLimitOverride">Optional season-level runtime limit override in minutes.</param>
    /// <param name="previewFromCreditsEndOverride">Optional season-level setting for deriving a Preview segment from Credits.</param>
    /// <param name="subtitleRecapDetectionOverride">Optional season-level setting for subtitle recap detection.</param>
    /// <param name="subtitlePreviewDetectionOverride">Optional season-level setting for subtitle preview detection.</param>
    public QueueVerifier(
        PluginConfiguration config,
        IReadOnlyCollection<AnalysisMode> modes,
        SeasonQueueSnapshot snapshot,
        bool ffmpegValid,
        int? analysisPercentOverride = null,
        int? analysisLengthLimitOverride = null,
        bool? previewFromCreditsEndOverride = null,
        bool? subtitleRecapDetectionOverride = null,
        bool? subtitlePreviewDetectionOverride = null)
    {
        _config = config;
        _modes = modes;
        _snapshot = snapshot;
        _ffmpegValid = ffmpegValid;
        _actionByMode = new Dictionary<AnalysisMode, AnalyzerAction>(AllModes.Length);
        _expectedHashByMode = new Dictionary<AnalysisMode, string>(AllModes.Length);
        _availableHashByMode = ffmpegValid ? null : new Dictionary<AnalysisMode, string>(AllModes.Length);
        _availableSubtitleIndependentHashByMode = ffmpegValid ? null : new Dictionary<AnalysisMode, string>();
        _availableOtherSourceHashByMode = ffmpegValid ? null : new Dictionary<AnalysisMode, string>();
        var otherSource = config.ChapterSource == ChapterSource.Embedded ? ChapterSource.JellyfinTable : ChapterSource.Embedded;
        foreach (var mode in AllModes)
        {
            var action = snapshot.AnalyzerActionByMode.TryGetValue(mode, out var savedAction) ? savedAction : AnalyzerAction.Default;
            _actionByMode[mode] = action;
            _expectedHashByMode[mode] = Hash(mode, action, ffmpegValid);
            _availableHashByMode?.Add(mode, Hash(mode, action, available: true));
            if (mode is AnalysisMode.Recap or AnalysisMode.Preview)
            {
                _subtitleIndependentHashByMode[mode] = Hash(mode, action, ffmpegValid, includeSubtitleSettings: false);
                _availableSubtitleIndependentHashByMode?.Add(mode, Hash(mode, action, available: true, includeSubtitleSettings: false));
            }

            if (action != AnalyzerAction.None)
            {
                _otherSourceHashByMode[mode] = Hash(mode, action, ffmpegValid, chapterSource: otherSource);
                _availableOtherSourceHashByMode?.Add(mode, Hash(mode, action, available: true, chapterSource: otherSource));
            }
        }

        // The season's overrides are the same for every hash of the instance.
        string Hash(AnalysisMode mode, AnalyzerAction action, bool available, bool includeSubtitleSettings = true, ChapterSource? chapterSource = null)
            => ConfigHasher.Analysis(
                config,
                mode,
                action,
                available,
                analysisPercentOverride,
                analysisLengthLimitOverride,
                previewFromCreditsEndOverride,
                subtitleRecapDetectionOverride,
                subtitlePreviewDetectionOverride,
                includeSubtitleSettings,
                chapterSource);
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
        var shortcutChanged = ClassifyShortcutPath(candidate);
        var (fileChanged, reopened) = ClassifyFileVersion(candidate);
        fileChanged |= shortcutChanged;
        candidate.FileChanged |= shortcutChanged;
        foreach (var mode in _modes)
        {
            // An empty hash is equivalent to no durable analysis state. It can be present on
            // rows created before hashing was recorded and must not settle an item forever.
            var hasAnalyzedHash = _snapshot.AnalysisRecords.TryGetValue((candidate.EpisodeId, mode), out var record)
                && !string.IsNullOrEmpty(record.ConfigHash);
            var hashMatches = hasAnalyzedHash && IsCurrentHash(record.ConfigHash, mode);
            string? sourceCheckHash = null;
            var otherSourceHash = hasAnalyzedHash && !hashMatches && IsOtherSourceHash(record.ConfigHash, mode, out sourceCheckHash);
            var subtitleOnlyHashChange = hasAnalyzedHash && !hashMatches && IsConventionalHashCurrent(record.ConfigHash, mode);

            if (hasAnalyzedHash && !fileChanged)
            {
                // A record from the other chapter source is no configuration change to report.
                var mismatch = !hashMatches && !otherSourceHash;
                if (!_storedHashByMode.TryGetValue(mode, out var stored) || (mismatch && !stored.Mismatch))
                {
                    _storedHashByMode[mode] = (record.ConfigHash, mismatch);
                }
            }

            // A record made for a different version of the file, or of the sidecars a
            // subtitle-detecting mode reads, settles nothing, whatever its hash says.
            if (fileChanged || reopened.Contains(mode))
            {
                hashMatches = false;
                otherSourceHash = false;
                subtitleOnlyHashChange = false;
            }
            else if (candidate.NeedsSubtitleOnlyReanalysis(mode))
            {
                hashMatches = false;
                otherSourceHash = false;
            }

            // A record from the other chapter source settles the episode until the season pass
            // has compared the two sources' chapters.
            var settled = hashMatches || otherSourceHash;
            if (_snapshot.SegmentModesByEpisodeId.TryGetValue(candidate.EpisodeId, out var modesWithSegments) &&
                modesWithSegments.Contains(mode))
            {
                var isUserProvided = _snapshot.UserProvidedByMode.TryGetValue(mode, out var userProvided) &&
                                     userProvided.Contains(candidate.EpisodeId);

                // Always preserve user-provided segments. Automatic results are reusable only
                // when the stored per-item hash still describes the current configuration.
                if (isUserProvided || settled)
                {
                    candidate.SetAnalyzed(mode, isUserProvided ? EpisodeState.UserProvided : EpisodeState.Analyzed);
                }
            }
            else if (settled)
            {
                candidate.SetAnalyzed(mode, EpisodeState.NoSegments);
            }

            // A user segment settles the mode whatever the record says, so its record is neither
            // checked nor re-stamped.
            if (sourceCheckHash is not null && candidate.GetAnalyzed(mode) is EpisodeState.Analyzed or EpisodeState.NoSegments)
            {
                candidate.MarkChapterSourceCheck(mode, new ChapterSourceCheck(record.ConfigHash, sourceCheckHash, _config.ChapterSource));
            }

            if (subtitleOnlyHashChange && candidate.GetAnalyzed(mode) == EpisodeState.NotAnalyzed)
            {
                candidate.MarkSubtitleOnlyReanalysis(mode);
            }
        }
    }

    /// <summary>
    /// Compares the episode's file versions with every record the item has, whatever the
    /// record's mode: a record of a disabled mode still proves the file changed, and its
    /// stale segments still have to go.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>A record of a mode that records the media version (every mode but a
    /// Recap or Preview with active subtitle detection) flags
    /// <see cref="QueuedEpisode.FileChanged"/> when it carries another media version, except a
    /// Recap or Preview record whose hash is not current: it may still carry the sidecar version
    /// a subtitle-detecting pass recorded.</description></item>
    /// <item><description>A Recap or Preview record whose subtitle detection is active must carry
    /// <see cref="QueuedEpisode.SubtitleFileVersion"/>. A record that does not (a sidecar was
    /// added, rewritten or removed, or detection was off when it was written) reopens its mode.
    /// The mode reruns subtitle detection alone when another record confirms the media version
    /// and the record's conventional hash is still current, and its full chain otherwise.</description></item>
    /// <item><description>Neither exception can tell a sidecar change from a replaced media file,
    /// so without a record that confirms the media version, either one flags
    /// <see cref="QueuedEpisode.FileChanged"/>.</description></item>
    /// <item><description>A record without a version predates versioning and is stamped with
    /// the media version, unless the file changed, in which case the reset rewrites it.</description></item>
    /// </list>
    /// Without a media version (Jellyfin holds no write time) no record can show a replaced
    /// file: a media-versioned record keeps its verdict, nothing is stamped, and a sidecar
    /// mismatch only reopens its mode.
    /// </remarks>
    /// <returns>Whether a record shows the media file was replaced, and the Recap and Preview
    /// modes reopened for their full chain by a sidecar version mismatch.</returns>
    private (bool FileChanged, IReadOnlySet<AnalysisMode> Reopened) ClassifyFileVersion(QueuedEpisode candidate)
    {
        var fileVersion = candidate.FileVersion;
        var needsBackfill = false;
        var mediaConfirmed = false;
        var ambiguousMismatch = false;
        List<(AnalysisMode Mode, string Hash)> sidecarMismatches = [];
        foreach (var mode in AllModes)
        {
            if (!_snapshot.AnalysisRecords.TryGetValue((candidate.EpisodeId, mode), out var record))
            {
                continue;
            }

            var subtitleVersioned = _config.ActiveSubtitlePattern(candidate, mode) is not null;
            if (record.FileVersion is not { } recorded)
            {
                // A record without a version cannot have covered a sidecar.
                if (subtitleVersioned && candidate.SubtitleFileVersion != fileVersion)
                {
                    sidecarMismatches.Add((mode, record.ConfigHash));
                }
                else
                {
                    needsBackfill = true;
                }
            }
            else if (subtitleVersioned)
            {
                // The sidecar version embeds the media version, so a match confirms it too.
                if (recorded == candidate.SubtitleFileVersion)
                {
                    mediaConfirmed = true;
                }
                else
                {
                    sidecarMismatches.Add((mode, record.ConfigHash));
                }
            }
            else if (fileVersion is { } media)
            {
                if (recorded == media)
                {
                    mediaConfirmed = true;
                }
                else if (mode is not (AnalysisMode.Recap or AnalysisMode.Preview) || IsCurrentHash(record.ConfigHash, mode))
                {
                    candidate.FileChanged = true;
                    return (true, new HashSet<AnalysisMode>());
                }
                else
                {
                    // Written under other settings, possibly with the sidecar version.
                    ambiguousMismatch = true;
                }
            }
        }

        // Neither mismatch can tell a sidecar change from a replaced media file.
        if (fileVersion is not null && !mediaConfirmed && (ambiguousMismatch || sidecarMismatches.Count > 0))
        {
            candidate.FileChanged = true;
            return (true, new HashSet<AnalysisMode>());
        }

        if (needsBackfill && fileVersion is { } version)
        {
            _fileVersionBackfill.TryAdd(candidate.EpisodeId, version);
        }

        HashSet<AnalysisMode> reopened = [];
        foreach (var (mode, hash) in sidecarMismatches)
        {
            if (mediaConfirmed && IsConventionalHashCurrent(hash, mode))
            {
                candidate.MarkSubtitleOnlyReanalysis(mode);
            }
            else
            {
                reopened.Add(mode);
            }
        }

        return (false, reopened);
    }

    private bool ClassifyShortcutPath(QueuedEpisode candidate)
    {
        var shortcutPath = candidate.IsShortcut && !string.IsNullOrEmpty(candidate.ShortcutPath)
            ? candidate.ShortcutPath
            : null;

        foreach (var mode in AllModes)
        {
            if (_snapshot.AnalysisRecords.TryGetValue((candidate.EpisodeId, mode), out var record)
                && !string.Equals(record.ShortcutPath, shortcutPath, StringComparison.Ordinal))
            {
                candidate.FileChanged = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the hash describes the current configuration of the mode, under the season's
    /// saved analyzer action. A failed FFmpeg capability probe must not invalidate good
    /// Chromaprint results: availability is an upward invalidation, so a later successful
    /// probe can reopen a season settled without Chromaprint, but a transient failed probe
    /// cannot discard results produced while it was available.
    /// </summary>
    private bool IsCurrentHash(string hash, AnalysisMode mode)
        => string.Equals(hash, _expectedHashByMode[mode], StringComparison.Ordinal)
            || (_availableHashByMode is { } availableHashByMode
                && string.Equals(hash, availableHashByMode[mode], StringComparison.Ordinal));

    /// <summary>
    /// Whether the hash describes the current configuration of the mode under the other
    /// chapter source, with the same Chromaprint-availability rule as <see cref="IsCurrentHash"/>.
    /// Such a record is settled pending the season pass's chapter source check, which re-stamps
    /// it with <paramref name="currentHash"/> when both sources hold the same chapters. A record
    /// written with Chromaprint available settles while the capability probe fails, but gets no
    /// check (<paramref name="currentHash"/> is <see langword="null"/>): the check could reopen
    /// the mode, and without Chromaprint the chain could not reproduce the result it would
    /// discard, so the check waits for a pass with Chromaprint.
    /// </summary>
    private bool IsOtherSourceHash(string hash, AnalysisMode mode, out string? currentHash)
    {
        currentHash = null;
        if (_otherSourceHashByMode.TryGetValue(mode, out var otherSourceHash)
            && string.Equals(hash, otherSourceHash, StringComparison.Ordinal))
        {
            currentHash = _expectedHashByMode[mode];
            return true;
        }

        return _availableOtherSourceHashByMode?.TryGetValue(mode, out var availableOtherSourceHash) == true
            && string.Equals(hash, availableOtherSourceHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a Recap or Preview hash still matches the current configuration in everything
    /// but the subtitle settings, so only subtitle detection needs to run again.
    /// </summary>
    private bool IsConventionalHashCurrent(string hash, AnalysisMode mode)
        => (_subtitleIndependentHashByMode.TryGetValue(mode, out var conventionalHash)
                && ConfigHasher.IsSubtitleOnlyHashChange(hash, conventionalHash))
            || (_availableSubtitleIndependentHashByMode?.TryGetValue(mode, out var availableConventionalHash) == true
                && ConfigHasher.IsSubtitleOnlyHashChange(hash, availableConventionalHash));

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

            // A pending episode whose record hash is current was reopened by its sidecar version.
            var hasStoredHash = _storedHashByMode.TryGetValue(mode, out var stored);
            var sidecarVersionChanged = !(hasStoredHash && stored.Mismatch)
                && verified.Any(episode => !episode.FileChanged
                    && episode.GetAnalyzed(mode) == EpisodeState.NotAnalyzed
                    && _snapshot.AnalysisRecords.TryGetValue((episode.EpisodeId, mode), out var record)
                    && IsCurrentHash(record.ConfigHash, mode));
            if (sidecarVersionChanged)
            {
                LogSeasonSubtitleFilesChanged(logger, mode, pending, verified.Count, first.SeriesName, first.SeasonNumber);
            }
            else if (hasStoredHash && stored.Mismatch)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Queuing {Count} of {Total} items in {Name} season {Season} for analysis: subtitle files changed since analysis")]
    private static partial void LogSeasonSubtitleFilesChanged(ILogger logger, AnalysisMode mode, int count, int total, string name, int season);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-analyzing {Count} of {Total} items in {Name} season {Season}: media file changed since analysis")]
    private static partial void LogSeasonFilesChanged(ILogger logger, int count, int total, string name, int season);
}
