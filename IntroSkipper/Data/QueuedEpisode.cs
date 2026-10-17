// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Model.Entities;

namespace IntroSkipper.Data;

/// <summary>
/// Episode queued for analysis.
/// </summary>
public sealed class QueuedEpisode
{
    private readonly EpisodeState[] _isAnalyzed = new EpisodeState[Enum.GetValues<AnalysisMode>().Length];
    private readonly SubtitleOutcome[] _subtitleOutcomes = new SubtitleOutcome[Enum.GetValues<AnalysisMode>().Length];
    private readonly HashSet<AnalysisMode> _subtitleOnlyReanalysisModes = [];
    private readonly Dictionary<AnalysisMode, ChapterSourceCheck> _chapterSourceChecks = [];

    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    public int EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode id.
    /// </summary>
    public Guid EpisodeId { get; set; }

    /// <summary>
    /// Gets or sets the season id.
    /// </summary>
    public Guid SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the series id.
    /// </summary>
    public Guid SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the full path to episode.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item's internal metadata folder, where Jellyfin saves the subtitles it
    /// downloads when the library does not save them with the media. Subtitle detection reads
    /// the text sidecars there as well as those next to the media file. Null when unknown.
    /// </summary>
    public string? InternalMetadataPath { get; set; }

    /// <summary>
    /// Gets or sets the name of the episode.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the category for this media item.
    /// </summary>
    public QueuedMediaCategory Category { get; set; } = QueuedMediaCategory.Episode;

    /// <summary>
    /// Gets or sets a value indicating whether this media item should be excluded from analysis.
    /// </summary>
    public bool IsExcluded { get; set; }

    /// <summary>
    /// Gets or sets the timestamp (in seconds) to stop searching for an introduction at.
    /// </summary>
    public double IntroFingerprintEnd { get; set; }

    /// <summary>
    /// Gets or sets the optional season-level percentage override used to build the
    /// fingerprint window and analysis hash.
    /// </summary>
    public int? AnalysisPercentOverride { get; set; }

    /// <summary>
    /// Gets or sets the optional season-level runtime override used to build the
    /// fingerprint window and analysis hash.
    /// </summary>
    public int? AnalysisLengthLimitOverride { get; set; }

    /// <summary>
    /// Gets or sets the optional season-level setting that derives a Preview segment from the
    /// end of Credits.
    /// </summary>
    public bool? PreviewFromCreditsEndOverride { get; set; }

    /// <summary>
    /// Gets or sets the optional season-level setting for subtitle recap detection.
    /// </summary>
    public bool? SubtitleRecapDetectionOverride { get; set; }

    /// <summary>
    /// Gets or sets the optional season-level setting for subtitle preview detection.
    /// </summary>
    public bool? SubtitlePreviewDetectionOverride { get; set; }

    /// <summary>
    /// Gets or sets the timestamp (in seconds) to start looking for end credits at.
    /// </summary>
    public double CreditsFingerprintStart { get; set; }

    /// <summary>
    /// Gets or sets the timestamp (in seconds) to stop looking for end credits at.
    /// This can differ from <see cref="Duration"/> when the container runtime is longer than the audio stream.
    /// </summary>
    public double CreditsFingerprintEnd { get; set; }

    /// <summary>
    /// Gets or sets the total duration of this media file (in seconds).
    /// </summary>
    public double Duration { get; set; }

    /// <summary>
    /// Gets or sets the chapters analysis reads for this item in the current pass. The season
    /// pass reads them from the chapter source before any mode runs: Jellyfin's chapter table,
    /// or the chapters embedded in the media file while <c>UseEmbeddedChaptersForAnalysis</c>
    /// is on. Analyzers read chapters only here and never fetch them. Empty until the pass
    /// reads them, and when the source has none.
    /// </summary>
    public IReadOnlyList<ChapterInfo> Chapters { get; set; } = [];

    /// <summary>
    /// Gets or sets the chapter source <see cref="Chapters"/> were read from in the current pass,
    /// <see langword="null"/> while nothing was read. The analysis record a mode writes names this
    /// source, not the setting at the moment the mode ran, so a setting changed while the pass
    /// runs leaves the episode to the chapter source check on the next pass.
    /// </summary>
    public ChapterSource? ChaptersSource { get; set; }

    /// <summary>
    /// Gets the modes whose record settles the episode under the other chapter source's hash
    /// and awaits the season pass's chapter source check, each with its stored hash and the hash
    /// the pass re-stamps it to when both sources hold the same chapters.
    /// </summary>
    public IReadOnlyDictionary<AnalysisMode, ChapterSourceCheck> ChapterSourceChecks => _chapterSourceChecks;

    /// <summary>
    /// Gets or sets the UTC time this item became available to analysis. This intentionally tracks
    /// Jellyfin's created timestamp; metadata refreshes update <c>DateLastSaved</c> and must not make
    /// an already-settled season look newly updated.
    /// </summary>
    public DateTime DateAdded { get; set; }

    /// <summary>
    /// Gets or sets the configuration hash for the current analysis pass.
    /// </summary>
    public string AnalysisConfigHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file version Jellyfin currently holds for the item: the ticks of
    /// the media file's last write time as of its last refresh. Null when Jellyfin holds
    /// none. Recorded with the analysis so a later file replacement reopens the item.
    /// </summary>
    public long? FileVersion { get; set; }

    /// <summary>
    /// Gets or sets <see cref="FileVersion"/> extended with the item's text subtitle
    /// sidecars (<c>SubtitleSidecarFiles.FileVersion</c>). It equals
    /// <see cref="FileVersion"/> when there are none, and is null when no mode of the pass
    /// has active subtitle detection. <c>PluginConfiguration.RecordedFileVersion</c> picks it
    /// for a mode whose subtitle detection is active, so a changed sidecar reopens only that
    /// mode.
    /// </summary>
    public long? SubtitleFileVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a stored analysis record shows the media file
    /// was replaced: a record of a mode other than Recap or Preview carries a file version
    /// other than <see cref="FileVersion"/>, or a Recap or Preview record whose mode has
    /// subtitle detection off carries another version under a hash that still matches the
    /// configuration. The pass then discards the item's automatic segments and fingerprints
    /// before analyzing it again. Any other mismatched Recap or Preview record leaves it unset
    /// and reopens only its own mode, since its version can also cover the sidecars
    /// (<see cref="SubtitleFileVersion"/>) or predate a change to subtitle detection.
    /// </summary>
    public bool FileChanged { get; set; }

    /// <summary>
    /// Sets the subtitle analyzer's outcome for a mode in this pass. The analyzer chain reads
    /// it after every analyzer has run to decide which stale rows to keep and how the mode settles.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="outcome">One of the enumeration values that specifies the outcome.</param>
    public void SetSubtitleOutcome(AnalysisMode mode, SubtitleOutcome outcome)
        => _subtitleOutcomes[(int)mode] = outcome;

    /// <summary>
    /// Gets the subtitle analyzer's outcome for a mode in this pass.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>The outcome; <see cref="SubtitleOutcome.None"/> when the analyzer recorded none.</returns>
    public SubtitleOutcome GetSubtitleOutcome(AnalysisMode mode)
        => _subtitleOutcomes[(int)mode];

    /// <summary>
    /// Marks that only what subtitle detection reads changed since this mode was analyzed: its
    /// subtitle settings, or a sidecar of an unchanged media file. The conventional result
    /// remains valid and is retained while subtitle detection is refreshed.
    /// </summary>
    /// <param name="mode">Analysis mode whose subtitle settings changed.</param>
    public void MarkSubtitleOnlyReanalysis(AnalysisMode mode)
        => _subtitleOnlyReanalysisModes.Add(mode);

    /// <summary>
    /// Determines whether only subtitle settings changed for this mode.
    /// </summary>
    /// <param name="mode">Analysis mode to inspect.</param>
    /// <returns>Whether conventional analyzer output can be reused.</returns>
    public bool NeedsSubtitleOnlyReanalysis(AnalysisMode mode)
        => _subtitleOnlyReanalysisModes.Contains(mode);

    /// <summary>
    /// Clears the subtitle-only shortcut when conventional output is being regenerated.
    /// </summary>
    /// <param name="mode">Analysis mode whose conventional result is being reset.</param>
    public void ClearSubtitleOnlyReanalysis(AnalysisMode mode)
        => _subtitleOnlyReanalysisModes.Remove(mode);

    /// <summary>
    /// Marks that the mode's record was written from the other chapter source, so the season
    /// pass compares the two sources' chapters before the episode is analyzed again.
    /// </summary>
    /// <param name="mode">Analysis mode whose record names the other chapter source.</param>
    /// <param name="check">The record's hash, the hash the current settings produce, and the source that hash names.</param>
    public void MarkChapterSourceCheck(AnalysisMode mode, ChapterSourceCheck check)
        => _chapterSourceChecks[mode] = check;

    /// <summary>
    /// Clears the chapter source checks once the season pass has resolved them.
    /// </summary>
    public void ClearChapterSourceChecks() => _chapterSourceChecks.Clear();

    /// <summary>
    /// Sets a value indicating whether this media has been already analyzed.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="value">Value to set.</param>
    public void SetAnalyzed(AnalysisMode mode, EpisodeState value)
    {
        _isAnalyzed[(int)mode] = value;
    }

    /// <summary>
    /// Sets a value indicating whether this media has been already analyzed.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>Value of the analyzed mode.</returns>
    public EpisodeState GetAnalyzed(AnalysisMode mode)
    {
        return _isAnalyzed[(int)mode];
    }

    /// <summary>
    /// Returns <see langword="true"/> when the episode still needs automatic analysis for
    /// the given mode — i.e. it is neither already <see cref="EpisodeState.Analyzed"/>
    /// nor protected by a <see cref="EpisodeState.UserProvided"/> segment.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns><see langword="true"/> if the episode should be included in the analysis queue.</returns>
    public bool NeedsAnalysis(AnalysisMode mode)
        => GetAnalyzed(mode) is not (EpisodeState.Analyzed or EpisodeState.UserProvided);

    /// <summary>
    /// Gets the time range (in seconds) that is fingerprinted for the given analysis mode.
    /// This is the single source of truth for detection cache keys: the exact values returned
    /// here are used both when writing cache entries and when looking them up, so the cached
    /// Start/End doubles always round-trip bit-exactly.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>Start and end timestamps of the fingerprint range.</returns>
    /// <exception cref="ArgumentException">If the analysis mode has no fingerprint range.</exception>
    public (double Start, double End) GetFingerprintRange(AnalysisMode mode)
    {
        return mode switch
        {
            AnalysisMode.Introduction => (0, IntroFingerprintEnd),
            AnalysisMode.Recap => (0, IntroFingerprintEnd),
            AnalysisMode.Credits => (CreditsFingerprintStart, CreditsFingerprintEnd > 0 ? CreditsFingerprintEnd : Duration),
            _ => throw new ArgumentException("Unknown analysis mode " + mode),
        };
    }

    /// <summary>
    /// Maps an analysis mode to the mode its Chromaprint fingerprint is cached under. Recap
    /// fingerprints the same opening window as Introduction, so both read and write one row
    /// instead of decoding the same audio twice. Rows older releases wrote under the Recap key
    /// are still read and copied under the shared key on first use.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>The mode used for the fingerprint cache key.</returns>
    public static AnalysisMode FingerprintCacheMode(AnalysisMode mode)
        => mode == AnalysisMode.Recap ? AnalysisMode.Introduction : mode;
}
