// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
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
/// anything else stays <see cref="EpisodeState.NotAnalyzed"/>. With
/// <see cref="PluginConfiguration.KeepExistingAnalysis"/> on, a record whose keep hash is current
/// settles too and keeps its configuration hash (<see cref="Judge"/>). A record whose file
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
    private readonly Dictionary<AnalysisMode, RecordHashes> _currentByMode;
    private readonly Dictionary<AnalysisMode, string> _subtitleIndependentHashByMode = [];
    private readonly Dictionary<AnalysisMode, string>? _availableSubtitleIndependentHashByMode;
    private readonly Dictionary<AnalysisMode, RecordHashes>? _availableByMode;

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

    private readonly Dictionary<(AnalysisMode Mode, RecordHashes Hashes), List<Guid>> _keepConfirmations = [];

    private static readonly AnalysisMode[] AllModes = Enum.GetValues<AnalysisMode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueVerifier"/> class.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="modes">Analysis modes of the run.</param>
    /// <param name="snapshot">The season's stored analysis state.</param>
    /// <param name="ffmpegValid">Whether the Chromaprint capability probe succeeded.</param>
    /// <param name="season">The season's hash inputs, as the writer hashes them.</param>
    public QueueVerifier(
        PluginConfiguration config,
        IReadOnlyCollection<AnalysisMode> modes,
        SeasonQueueSnapshot snapshot,
        bool ffmpegValid,
        SeasonHashInputs season)
    {
        _config = config;
        _modes = modes;
        _snapshot = snapshot;
        _ffmpegValid = ffmpegValid;
        _actionByMode = new Dictionary<AnalysisMode, AnalyzerAction>(AllModes.Length);
        _currentByMode = new Dictionary<AnalysisMode, RecordHashes>(AllModes.Length);
        _availableByMode = ffmpegValid ? null : new Dictionary<AnalysisMode, RecordHashes>(AllModes.Length);
        _availableSubtitleIndependentHashByMode = ffmpegValid ? null : new Dictionary<AnalysisMode, string>();
        _availableOtherSourceHashByMode = ffmpegValid ? null : new Dictionary<AnalysisMode, string>();
        var otherSource = config.ChapterSource == ChapterSource.Embedded ? ChapterSource.JellyfinTable : ChapterSource.Embedded;
        foreach (var mode in AllModes)
        {
            var action = snapshot.AnalyzerActionByMode.TryGetValue(mode, out var savedAction) ? savedAction : AnalyzerAction.Default;
            _actionByMode[mode] = action;
            _currentByMode[mode] = ConfigHasher.Record(config, mode, action, ffmpegValid, season);
            _availableByMode?.Add(mode, ConfigHasher.Record(config, mode, action, ffmpegValid: true, season));
            if (mode is AnalysisMode.Recap or AnalysisMode.Preview)
            {
                _subtitleIndependentHashByMode[mode] = ConfigHasher.Analysis(config, mode, action, ffmpegValid, season, includeSubtitleSettings: false);
                _availableSubtitleIndependentHashByMode?.Add(mode, ConfigHasher.Analysis(config, mode, action, ffmpegValid: true, season, includeSubtitleSettings: false));
            }

            if (action != AnalyzerAction.None)
            {
                _otherSourceHashByMode[mode] = ConfigHasher.Analysis(config, mode, action, ffmpegValid, season, chapterSource: otherSource);
                _availableOtherSourceHashByMode?.Add(mode, ConfigHasher.Analysis(config, mode, action, ffmpegValid: true, season, chapterSource: otherSource));
            }
        }
    }

    private enum RecordVerdict
    {
        Missing,
        Current,
        Kept,
        OtherSource,
        SubtitleOnly,
        Stale,
    }

    /// <summary>
    /// Gets the episodes classified so far whose records carry no file version, each with
    /// the version to stamp on them, for <c>BackfillFileVersionsAsync</c>.
    /// </summary>
    public IReadOnlyDictionary<Guid, long> FileVersionBackfill => _fileVersionBackfill;

    /// <summary>
    /// Gets the records classified so far as settled whose stored keep hash is not the one to
    /// store, for <c>ConfirmKeepHashesAsync</c>.
    /// </summary>
    public IReadOnlyCollection<KeepConfirmation> KeepConfirmations
        => [.. _keepConfirmations.Select(entry => new KeepConfirmation(entry.Key.Mode, entry.Key.Hashes, entry.Value))];

    /// <summary>
    /// Sets the candidate's per-mode analysis state from the season snapshot.
    /// </summary>
    /// <param name="candidate">A queued episode that exists on disk and is not excluded.</param>
    public void Classify(QueuedEpisode candidate)
    {
        var (fileChanged, reopened) = ClassifyFileVersion(candidate);
        foreach (var mode in _modes)
        {
            var judgement = _snapshot.AnalysisRecords.TryGetValue((candidate.EpisodeId, mode), out var record)
                ? Judge(record, mode)
                : new Judgement(RecordVerdict.Missing);

            if (judgement.Verdict != RecordVerdict.Missing && !fileChanged)
            {
                // A record from the other chapter source is no configuration change to report.
                var mismatch = judgement.Verdict is RecordVerdict.Stale or RecordVerdict.SubtitleOnly;
                if (!_storedHashByMode.TryGetValue(mode, out var stored) || (mismatch && !stored.Mismatch))
                {
                    _storedHashByMode[mode] = (record.ConfigHash, mismatch);
                }
            }

            if (fileChanged || reopened.Contains(mode) || candidate.NeedsSubtitleOnlyReanalysis(mode))
            {
                judgement = new Judgement(RecordVerdict.Stale);
            }

            // A record from the other chapter source settles the episode until the season pass
            // has compared the two sources' chapters.
            var settled = judgement.Verdict is RecordVerdict.Current or RecordVerdict.Kept or RecordVerdict.OtherSource;
            if (_snapshot.SegmentModesByEpisodeId.TryGetValue(candidate.EpisodeId, out var modesWithSegments) &&
                modesWithSegments.Contains(mode))
            {
                var isUserProvided = _snapshot.UserProvidedByMode.TryGetValue(mode, out var userProvided) &&
                                     userProvided.Contains(candidate.EpisodeId);

                if (isUserProvided || settled)
                {
                    candidate.SetAnalyzed(mode, isUserProvided ? EpisodeState.UserProvided : EpisodeState.Analyzed);
                }
            }
            else if (settled)
            {
                candidate.SetAnalyzed(mode, EpisodeState.NoSegments);
            }

            var state = candidate.GetAnalyzed(mode);
            if (state is EpisodeState.Analyzed or EpisodeState.NoSegments)
            {
                if (judgement.SourceCheckHash is { } sourceCheckHash)
                {
                    candidate.MarkChapterSourceCheck(mode, new ChapterSourceCheck(record.ConfigHash, sourceCheckHash, _config.ChapterSource));
                }

                if (judgement.ConfirmAs is { } hashes && !string.Equals(record.KeepHash, hashes.KeepHash, StringComparison.Ordinal))
                {
                    if (!_keepConfirmations.TryGetValue((mode, hashes), out var itemIds))
                    {
                        itemIds = [];
                        _keepConfirmations[(mode, hashes)] = itemIds;
                    }

                    itemIds.Add(candidate.EpisodeId);
                }
            }

            if (judgement.Verdict == RecordVerdict.SubtitleOnly && state == EpisodeState.NotAnalyzed)
            {
                candidate.MarkSubtitleOnlyReanalysis(mode);
            }
        }
    }

    private Judgement Judge(AnalysisRecord record, AnalysisMode mode)
    {
        if (string.IsNullOrEmpty(record.ConfigHash))
        {
            return new Judgement(RecordVerdict.Missing);
        }

        if (TryCurrent(record.ConfigHash, mode, out var matched))
        {
            return new Judgement(RecordVerdict.Current, ConfirmAs: matched);
        }

        if (_config.KeepExistingAnalysis)
        {
            var current = _currentByMode[mode];
            RecordHashes? available = _availableByMode?[mode];
            if (record.KeepHash is null)
            {
                return new Judgement(RecordVerdict.Kept, ConfirmAs: new RecordHashes(record.ConfigHash, (available ?? current).KeepHash));
            }

            if (string.Equals(record.KeepHash, current.KeepHash, StringComparison.Ordinal)
                || string.Equals(record.KeepHash, available?.KeepHash, StringComparison.Ordinal))
            {
                return new Judgement(RecordVerdict.Kept);
            }
        }

        if (IsOtherSourceHash(record.ConfigHash, mode, out var sourceCheckHash))
        {
            return new Judgement(RecordVerdict.OtherSource, sourceCheckHash);
        }

        return new Judgement(IsConventionalHashCurrent(record.ConfigHash, mode) ? RecordVerdict.SubtitleOnly : RecordVerdict.Stale);
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
                else if (mode is not (AnalysisMode.Recap or AnalysisMode.Preview) || TryCurrent(record.ConfigHash, mode, out _))
                {
                    candidate.FileChanged = true;
                    return (true, new HashSet<AnalysisMode>());
                }
                else
                {
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

    /// <summary>
    /// Finds the current record hashes whose configuration hash the stored one matches, under
    /// the season's saved analyzer action. A failed FFmpeg capability probe must not invalidate
    /// good Chromaprint results: availability is an upward invalidation, so a later successful
    /// probe can reopen a season settled without Chromaprint, but a transient failed probe
    /// cannot discard results produced while it was available. While the probe fails, a hash
    /// made with Chromaprint available matches its own record hashes.
    /// </summary>
    private bool TryCurrent(string hash, AnalysisMode mode, out RecordHashes current)
    {
        current = _currentByMode[mode];
        if (string.Equals(hash, current.ConfigHash, StringComparison.Ordinal))
        {
            return true;
        }

        if (_availableByMode is { } availableByMode && string.Equals(hash, availableByMode[mode].ConfigHash, StringComparison.Ordinal))
        {
            current = availableByMode[mode];
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the hash describes the current configuration of the mode under the other
    /// chapter source, with the same Chromaprint-availability rule as <see cref="TryCurrent"/>.
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
            currentHash = _currentByMode[mode].ConfigHash;
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

            var hasStoredHash = _storedHashByMode.TryGetValue(mode, out var stored);
            var sidecarVersionChanged = !(hasStoredHash && stored.Mismatch)
                && verified.Any(episode => !episode.FileChanged
                    && episode.GetAnalyzed(mode) == EpisodeState.NotAnalyzed
                    && _snapshot.AnalysisRecords.TryGetValue((episode.EpisodeId, mode), out var record)
                    && Judge(record, mode).Verdict is RecordVerdict.Current or RecordVerdict.Kept);
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
                    _currentByMode[mode].ConfigHash,
                    ConfigHasher.CarriesChromaprintAvailability(mode) ? _ffmpegValid.ToString() : "n/a");
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "[Mode: {Mode}] Queuing {Count} of {Total} items in {Name} season {Season} for analysis: {Reason}")]
    private static partial void LogSeasonQueuedForAnalysis(ILogger logger, AnalysisMode mode, int count, int total, string name, int season, AnalysisReason reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Queuing {Count} of {Total} items in {Name} season {Season} for analysis: analysis configuration hash changed from \"{StoredHash}\" to \"{ExpectedHash}\" (chromaprint available: {ChromaprintAvailable})")]
    private static partial void LogSeasonConfigHashChanged(ILogger logger, AnalysisMode mode, int count, int total, string name, int season, string storedHash, string expectedHash, string chromaprintAvailable);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Queuing {Count} of {Total} items in {Name} season {Season} for analysis: subtitle files changed since analysis")]
    private static partial void LogSeasonSubtitleFilesChanged(ILogger logger, AnalysisMode mode, int count, int total, string name, int season);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-analyzing {Count} of {Total} items in {Name} season {Season}: media file changed since analysis")]
    private static partial void LogSeasonFilesChanged(ILogger logger, int count, int total, string name, int season);

    private readonly record struct Judgement(RecordVerdict Verdict, string? SourceCheckHash = null, RecordHashes? ConfirmAs = null);
}
