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
/// the dashboard derive each season's value from the old global switch. For seasons in this
/// migration's library snapshot that is the value it stores. A season added while it runs
/// is not in the snapshot: it follows the old switch until the marker is written, then
/// defaults to disabled like any other unseen season.
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
                var enabled = config.AnimePreviewFromCreditsEnd
                    && season.Episodes.Any(episode => episode.Category == QueuedMediaCategory.AnimeEpisode);

                // The dashboard is live while this runs, so the write fills only a missing
                // choice and never reads and rewrites the season's other overrides.
                await _database.SetPreviewFromCreditsEndIfUnsetAsync(season.Key, enabled, stoppingToken).ConfigureAwait(false);
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
