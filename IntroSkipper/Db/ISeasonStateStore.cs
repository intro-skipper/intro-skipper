// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Persistence operations for per-season analysis state (<see cref="DbSeasonState"/>).
/// The season is treated as the aggregate root for analysis bookkeeping, so the two operations that
/// atomically touch segments and season state together (<see cref="ResetSeasonForReanalysisAsync"/>
/// and <see cref="GetSeasonQueueSnapshotAsync"/>) live here rather than being split across stores,
/// which would break their single-transaction / single-connection semantics.
/// </summary>
public interface ISeasonStateStore
{
    /// <summary>
    /// Creates or updates the analyzer action for each supplied mode of a season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="analyzerActions">Analyzer actions keyed by analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task SetAnalyzerActionsAsync(Guid seasonId, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the analyzed-episode list and configuration hash for a season and mode.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="episodeIds">Episode IDs analyzed with the current configuration.</param>
    /// <param name="configHash">Configuration hash used for the analysis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task SetEpisodeIdsAsync(Guid seasonId, AnalysisMode mode, IEnumerable<Guid> episodeIds, string configHash = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a single episode ID from the season's analyzed-state list for the given mode.
    /// The read and write share one context to keep the window for concurrent overwrites as small
    /// as possible, and the write is skipped entirely when the ID is not present in the stored list.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="episodeId">Episode ID to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RemoveEpisodeIdAsync(Guid seasonId, AnalysisMode mode, Guid episodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the analyzed-episode lists for a season, keyed by analysis mode.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Episode IDs keyed by analysis mode.</returns>
    Task<IReadOnlyDictionary<AnalysisMode, IEnumerable<Guid>>> GetEpisodeIdsAsync(Guid seasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the settled-season reanalysis state for all modes in a season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Settled reanalysis state keyed by analysis mode.</returns>
    Task<IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>> GetSettleReanalysisStatesAsync(Guid seasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that season analysis modes have been re-analyzed for the given episode set so the
    /// exact completed set is not repeated on subsequent scans or after a plugin restart. Call only
    /// after the reset has committed.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="modes">Analysis modes that were re-analyzed.</param>
    /// <param name="episodeIds">Episode IDs that were re-analyzed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RecordSettleReanalysisAsync(Guid seasonId, IReadOnlyCollection<AnalysisMode> modes, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears stored automatic analysis state for a season so it is re-analyzed from scratch on the
    /// current pass. Automatic segments for the supplied modes are deleted and the season's analyzed
    /// episode lists are cleared; user-provided segments are preserved. The deletes and the
    /// episode-list clear run in a single transaction so a cancelled reset cannot leave segments
    /// deleted while their episodes are still recorded as analyzed.
    /// </summary>
    /// <param name="seasonId">Season ID whose analyzed-state lists should be cleared.</param>
    /// <param name="episodeIds">Episode IDs whose automatic segments should be removed.</param>
    /// <param name="modes">Analysis modes to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task ResetSeasonForReanalysisAsync(Guid seasonId, IEnumerable<Guid> episodeIds, IReadOnlyCollection<AnalysisMode> modes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the season state rows and all segments for the supplied episodes in one round trip,
    /// producing the queue verification snapshot.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="episodeIds">Episode IDs in the season.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The season queue snapshot.</returns>
    Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the analyzer action for every analysis mode of a season, defaulting missing modes to
    /// <see cref="AnalyzerAction.Default"/>.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analyzer actions keyed by analysis mode.</returns>
    Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(Guid seasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the analyzer action for a season and mode, or <see cref="AnalyzerAction.Default"/> when none is stored.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The analyzer action.</returns>
    Task<AnalyzerAction> GetAnalyzerActionAsync(Guid seasonId, AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes season state rows whose season is not in <paramref name="validSeasonIds"/>.
    /// Safe for arbitrarily large ID sets: the collection is sent as a single JSON parameter.
    /// </summary>
    /// <param name="validSeasonIds">Season IDs whose state should be kept.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task CleanSeasonStatesAsync(IReadOnlyCollection<Guid> validSeasonIds, CancellationToken cancellationToken = default);
}
