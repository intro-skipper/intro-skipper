// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Episode queued for analysis.
/// </summary>
public sealed class QueuedEpisode
{
    private readonly EpisodeState[] _isAnalyzed = new EpisodeState[Enum.GetValues<AnalysisMode>().Length];

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
