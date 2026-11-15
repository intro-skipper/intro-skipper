// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// Copies the removed plugin-wide anime preview choice into every current season before
/// analysis starts. Existing explicit season choices are left untouched; seasons without a
/// choice keep the behavior they had under the old global switch. Once complete, unseen
/// seasons default to disabled.
/// </summary>
/// <param name="seasonResolver">Library season resolver.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="logger">Logger.</param>
public sealed class PreviewSettingMigration(
    SeasonResolver seasonResolver,
    IntroSkipperDatabase database,
    ILogger<PreviewSettingMigration> logger) : IHostedService
{
    private readonly SeasonResolver _seasonResolver = seasonResolver;
    private readonly IntroSkipperDatabase _database = database;
    private readonly ILogger<PreviewSettingMigration> _logger = logger;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (await _database.IsPreviewSettingMigrationCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var resolution = _seasonResolver.ResolveLibrary(includeExcluded: true, cancellationToken);
        foreach (var season in resolution.Seasons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var overrides = await _database.GetAnalysisOverridesAsync(season.Key, cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
        }

        if (resolution.Failures > 0)
        {
            _logger.LogWarning(
                "Could not migrate the legacy post-credits preview choice for every library; {FailureCount} library or series resolutions failed. The migration will retry at the next server start.",
                resolution.Failures);
            return;
        }

        await _database.CompletePreviewSettingMigrationAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
