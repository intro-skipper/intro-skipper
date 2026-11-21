// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// Copies the removed plugin-wide anime preview choice into every current season.
/// Existing explicit season choices are left untouched; seasons without a choice keep the
/// behavior they had under the old global switch. Once complete, unseen seasons default to
/// disabled.
/// </summary>
/// <remarks>
/// Runs in the background so a slow library walk or a database that cannot initialize
/// never holds up Jellyfin's startup. Until the completion marker is written, analysis and
/// the dashboard derive the same per-season value from the old global switch, so passes
/// that run first get the result this migration would have stored.
/// </remarks>
/// <param name="seasonResolver">Library season resolver.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="logger">Logger.</param>
public sealed class PreviewSettingMigration(
    SeasonResolver seasonResolver,
    IntroSkipperDatabase database,
    ILogger<PreviewSettingMigration> logger) : BackgroundService
{
    private readonly SeasonResolver _seasonResolver = seasonResolver;
    private readonly IntroSkipperDatabase _database = database;
    private readonly ILogger<PreviewSettingMigration> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var config = Plugin.Instance!.Configuration;
            if (await _database.IsPreviewSettingMigrationCompleteAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }

            var resolution = _seasonResolver.ResolveLibrary(includeExcluded: true, stoppingToken, includePluginDisabled: true, countUnresolvedItems: true);
            foreach (var season in resolution.Seasons)
            {
                stoppingToken.ThrowIfCancellationRequested();
                var overrides = await _database.GetAnalysisOverridesAsync(season.Key, stoppingToken).ConfigureAwait(false);
                if (overrides.PreviewFromCreditsEnd is not null)
                {
                    continue;
                }

                var enabled = config.AnimePreviewFromCreditsEnd
                    && season.Episodes.Any(episode => episode.Category == QueuedMediaCategory.AnimeEpisode);
                await _database.SetAnalysisOverridesAsync(
                    season.Key,
                    overrides.AnalysisPercent,
                    overrides.AnalysisLengthLimit,
                    enabled,
                    overrides.SubtitleRecapDetection,
                    overrides.SubtitlePreviewDetection,
                    stoppingToken).ConfigureAwait(false);
            }

            if (resolution.Failures > 0)
            {
                _logger.LogWarning(
                    "Could not migrate the legacy post-credits preview choice for every library; {FailureCount} library or series resolutions failed. The migration will retry at the next server start.",
                    resolution.Failures);
                return;
            }

            await _database.CompletePreviewSettingMigrationAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not migrate the legacy post-credits preview choice. The migration will retry at the next server start.");
        }
    }
}
