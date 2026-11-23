// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

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
/// never holds up Jellyfin's startup. It stores the seasons the old switch enabled and the
/// completion marker in one transaction, and only once every library resolves. Otherwise it
/// stores nothing and the next start retries.
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

            if (!config.AnimePreviewFromCreditsEnd)
            {
                await _database.CompletePreviewSettingMigrationAsync([], stoppingToken).ConfigureAwait(false);
                return;
            }

            var resolution = _seasonResolver.ResolveLibrary(includeExcluded: true, stoppingToken, includePluginDisabled: true, countUnresolvedItems: true);
            if (resolution.Failures > 0)
            {
                _logger.LogWarning(
                    "Could not migrate the legacy post-credits preview choice for every library; {FailureCount} library, series or item resolutions failed. The migration will retry at the next server start.",
                    resolution.Failures);
                return;
            }

            var enabled = resolution.Seasons
                .Where(season => config.LegacyPreviewFromCreditsEnd(season.Episodes))
                .Select(season => season.Key);
            await _database.CompletePreviewSettingMigrationAsync(enabled, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Only a shutdown cancellation may leave ExecuteAsync. Any other exception that
            // escapes it stops the host.
            _logger.LogError(ex, "Could not migrate the legacy post-credits preview choice. The migration will retry at the next server start.");
        }
    }
}
