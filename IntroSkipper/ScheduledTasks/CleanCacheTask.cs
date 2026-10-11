// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using IntroSkipper.SegmentChanges;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Clean the Intro Skipper cache of unused rows.
/// </summary>
/// <param name="logger">Logger.</param>
/// <param name="seasonResolver">Resolver of every enabled library's seasons.</param>
/// <param name="libraryManager">Library manager, used to check whether a stale-candidate id still resolves to a server item.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="cacheDatabase">Detection cache database facade.</param>
/// <param name="cacheService">Detection cache service; owns the configuration-hash policy.</param>
/// <param name="eraser">Erases the segments of items the server no longer knows and converges their mirrors.</param>
public partial class CleanCacheTask(
    ILogger<CleanCacheTask> logger,
    SeasonResolver seasonResolver,
    ILibraryManager libraryManager,
    IntroSkipperDatabase database,
    DetectionCacheDatabase cacheDatabase,
    DetectionCacheService cacheService,
    ISegmentEraser eraser) : IScheduledTask
{
    private readonly ILogger<CleanCacheTask> _logger = logger;
    private readonly SeasonResolver _seasonResolver = seasonResolver;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IntroSkipperDatabase _database = database;
    private readonly DetectionCacheDatabase _cacheDatabase = cacheDatabase;
    private readonly DetectionCacheService _cacheService = cacheService;
    private readonly ISegmentEraser _eraser = eraser;

    /// <summary>
    /// Gets the task name.
    /// </summary>
    public string Name => "Clean Intro Skipper Cache";

    /// <summary>
    /// Gets the task category.
    /// </summary>
    public string Category => "Intro Skipper";

    /// <summary>
    /// Gets the task description.
    /// </summary>
    public string Description => "Clear Intro Skipper cache of unused rows.";

    /// <summary>
    /// Gets the task key.
    /// </summary>
    public string Key => "CPBIntroSkipperCleanCache";

    /// <summary>
    /// Cleans the cache of unused rows.
    /// Clears segment, season-state and cache rows of items the server no longer knows.
    /// Items that still exist but were not resolved (a provider-disabled library, an
    /// episode waiting for its season) keep their segment and season-state rows. They lose
    /// only the cache rows no read path accepts, which are deleted for every item.
    /// </summary>
    /// <param name="progress">Task progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // The resolver already skips libraries where the plugin is disabled via
        // LibraryOptions.DisabledMediaSegmentProviders.
        var (seasons, failures) = _seasonResolver.ResolveLibrary(includeExcluded: true, cancellationToken);

        // Every cleanup below starts from rows that are NOT in the resolved seasons, so an
        // incomplete result would push swathes of healthy data through the stale-candidate
        // path and lean entirely on the per-id existence check below. Bail out instead.
        if (failures > 0)
        {
            LogSkippingCleanupEnumerationFailures(_logger, failures);
            progress.Report(100);
            return;
        }

        var enabledLibraryEpisodeIds = seasons
            .SelectMany(static season => season.Episodes)
            .Select(static episode => episode.EpisodeId)
            .ToHashSet();

        if (enabledLibraryEpisodeIds.Count == 0)
        {
            LogSkippingCleanupNoEnabledEpisodes(_logger);
            progress.Report(100);
            return;
        }

        // Absence from the resolved seasons only proves an id was not resolved: the
        // resolver skips provider-disabled libraries, whose rows (user segments,
        // tombstones, analyzer actions) must survive that reversible toggle. Only ids the
        // server itself no longer resolves are deleted. Season keys are checked the same
        // way: each is a real item id, a season's or a movie's.
        var existsById = new Dictionary<Guid, bool>();
        bool IsGone(Guid id)
        {
            if (!existsById.TryGetValue(id, out var exists))
            {
                exists = id != Guid.Empty && _libraryManager.GetItemById(id) is not null;
                existsById[id] = exists;
            }

            return !exists;
        }

        var staleTimestampEpisodeIds = (await _database
            .GetStaleTimestampEpisodeIdsAsync(enabledLibraryEpisodeIds, cancellationToken)
            .ConfigureAwait(false))
            .Where(IsGone)
            .ToList();

        if (staleTimestampEpisodeIds.Count > 0)
        {
            // Cache rows of gone items are cleaned below by their own query.
            await _eraser
                .EraseItemsAsync(staleTimestampEpisodeIds, eraseCache: false, cancellationToken)
                .ConfigureAwait(false);
        }

        // Identify episode IDs in the SQLite cache whose items are gone.
        var invalidEpisodeIds = (await _cacheDatabase
            .GetStaleItemIdsAsync(enabledLibraryEpisodeIds, cancellationToken)
            .ConfigureAwait(false))
            .Where(IsGone)
            .ToList();

        if (invalidEpisodeIds.Count > 0)
        {
            LogDeletingDetectionCacheRows(_logger, invalidEpisodeIds.Count);
            // Best-effort: the facade logs and swallows database errors.
            await _cacheDatabase
                .DeleteForItemsAsync(invalidEpisodeIds, cancellationToken)
                .ConfigureAwait(false);
        }

        // Clean up season state by removing seasons that no longer exist.
        var seasonKeys = seasons.Select(static season => season.Key).ToList();
        var staleSeasonIds = await _database.GetStaleSeasonIdsAsync(seasonKeys, cancellationToken).ConfigureAwait(false);
        var retainedSeasonIds = seasonKeys.Concat(staleSeasonIds.Where(id => !IsGone(id)));
        await _database.CleanSeasonStateAsync(retainedSeasonIds, cancellationToken).ConfigureAwait(false);

        // Per-item state (disable flags, analysis records) follows the item, not a season
        // key, so it is pruned against the retained item IDs instead.
        var staleItemStateIds = await _database.GetStaleItemStateIdsAsync(enabledLibraryEpisodeIds, cancellationToken).ConfigureAwait(false);
        var retainedItemIds = enabledLibraryEpisodeIds.Concat(staleItemStateIds.Where(id => !IsGone(id))).ToList();
        await _database.CleanItemStateAsync(retainedItemIds, cancellationToken).ConfigureAwait(false);

        // Cache rows whose configuration hash no read path accepts any more are dead weight;
        // hash-based, so independent of the item enumeration above.
        var unreadableRows = await _cacheService.DeleteUnreadableEntriesAsync(cancellationToken).ConfigureAwait(false);
        if (unreadableRows > 0)
        {
            LogDeletedUnreadableCacheRows(_logger, unreadableRows);
        }

        progress.Report(100);
    }

    /// <summary>
    /// Get task triggers.
    /// </summary>
    /// <returns>Task triggers.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleting detection cache rows for {Count} items the server no longer knows")]
    private static partial void LogDeletingDetectionCacheRows(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} detection cache rows that are unreadable under the current configuration")]
    private static partial void LogDeletedUnreadableCacheRows(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping cache cleanup: {Count} library(ies) or item(s) failed to enumerate, so stale-data detection would over-delete. Check the enumeration errors logged above and re-run the task")]
    private static partial void LogSkippingCleanupEnumerationFailures(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping cache cleanup: no episodes or movies were found in enabled libraries. To erase Intro Skipper data intentionally, use the erase actions on the plugin configuration page")]
    private static partial void LogSkippingCleanupNoEnabledEpisodes(ILogger logger);
}
