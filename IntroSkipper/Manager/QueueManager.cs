// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Manages enqueuing library items for analysis.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="QueueManager"/> class.
/// </remarks>
/// <param name="logger">Logger.</param>
/// <param name="libraryManager">Library manager.</param>
/// <param name="providerManager">Provider manager.</param>
/// <param name="fileSystem">File system.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="database">Segment database facade.</param>
internal partial class QueueManager(ILogger<QueueManager> logger, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, IFFmpegService ffmpegService, IIntroSkipperDatabase database)
{
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProviderManager _providerManager = providerManager;
    private readonly ILogger<QueueManager> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly Dictionary<Guid, List<QueuedEpisode>> _queuedEpisodes = [];

    // Queue key of the first episode queued per (series, aired season), so in-season
    // specials find their host season without scanning every queued season.
    private readonly Dictionary<(Guid SeriesId, int SeasonNumber), Guid> _seasonKeys = [];
    private readonly HashSet<Guid> _refreshedEpisodes = [];
    private bool? _ffmpegValid;
    private double _analysisPercent;
    private int _enumerationFailures;

    /// <summary>
    /// Gets the number of libraries or individual items that could not be enumerated or
    /// queued during the most recent <see cref="GetMediaInventoryAsync"/> call.
    /// A non-zero value means the inventory is incomplete; callers that delete data missing
    /// from the queue must not treat the affected items as stale.
    /// </summary>
    internal int EnumerationFailureCount => _enumerationFailures;

    // Per-run memo on top of the service's success-only memoization: while ffmpeg is
    // invalid the service re-probes every call, so cache the verdict here to keep an
    // analysis run at one probe instead of one per season.
    internal async Task<bool> GetFfmpegValidAsync(CancellationToken cancellationToken = default)
    {
        if (_ffmpegValid is { } cached)
        {
            return cached;
        }

        var ffmpegValid = await _ffmpegService.CheckFFmpegVersionAsync(cancellationToken).ConfigureAwait(false);
        _ffmpegValid = ffmpegValid;
        return ffmpegValid;
    }

    /// <summary>
    /// Enumerates the media inventory, grouped by season (movies under their own id).
    /// <see cref="EnumerationFailureCount"/> reports whether every library and item was
    /// inspected; cleanup must not delete rows absent from an incomplete inventory.
    /// </summary>
    /// <param name="includeExcluded">Whether excluded items should be included.</param>
    /// <param name="seasonIds">Season IDs and movie IDs to enqueue, or <see langword="null"/> to enqueue everything.</param>
    /// <param name="cancellationToken">Token used to cancel enumeration.</param>
    /// <returns>The enumerated media items keyed by season.</returns>
    internal async Task<IReadOnlyDictionary<Guid, List<QueuedEpisode>>> GetMediaInventoryAsync(
        bool includeExcluded = false,
        IReadOnlyCollection<Guid>? seasonIds = null,
        CancellationToken cancellationToken = default)
    {
        // Only runs with the standard exclusions publish to the plugin's global queue state: a
        // full enumeration replaces the published queue, a scoped run merges its seasons into it
        // (so seasons analyzed outside a full scan stay visible to the dashboard endpoints, which
        // serve from the published queue), and an excluded-inventory run must not count excluded
        // items so it publishes nothing.
        var publishQueue = !includeExcluded && seasonIds is null;
        var mergeQueue = !includeExcluded && seasonIds is not null;

        _enumerationFailures = 0;
        _queuedEpisodes.Clear();
        _seasonKeys.Clear();

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _enumerationFailures++;
            LogPluginInstanceNull(_logger);
            return _queuedEpisodes;
        }

        var config = plugin.Configuration;
        _analysisPercent = config.AnalysisPercent / 100.0;
        var policy = ExclusionPolicy.FromConfiguration(config);
        if (policy.BroadPathRootCount > 0)
        {
            LogBroadPathRootExclusions(_logger, policy.BroadPathRootCount);
        }

        if (seasonIds is { Count: 0 })
        {
            return _queuedEpisodes;
        }

        // For selected libraries, enqueue either all contained items or only the requested seasons/movies.
        var virtualFolders = _libraryManager.GetVirtualFolders();
        if (virtualFolders is null)
        {
            _enumerationFailures++;
            LogLibraryManagerNull(_logger);
            return _queuedEpisodes;
        }

        // Resolve each requested id to its owning libraries once, so a scoped run queries only
        // the folders that can contain a requested item instead of fanning its queries out to
        // every enabled library. The narrowing is an optimization, never a filter: collection-folder
        // resolution is path-based and can come up empty for an item the scoped queries would still
        // find (a broken parent chain, a library still being reparented), so a requested id that
        // resolves to no folder disables the narrowing for the whole run rather than silently
        // reducing it to zero libraries.
        HashSet<Guid>? scopedLibraryIds = null;
        if (seasonIds is not null)
        {
            scopedLibraryIds = [];
            foreach (var seasonId in seasonIds)
            {
                var resolvedFolders = seasonId != Guid.Empty && _libraryManager.GetItemById(seasonId) is { } requestedItem
                    ? _libraryManager.GetCollectionFolders(requestedItem)
                    : null;

                if (resolvedFolders is null or { Count: 0 })
                {
                    LogUnresolvedScopedItemLibrary(_logger, seasonId);
                    scopedLibraryIds = null;
                    break;
                }

                foreach (var collectionFolder in resolvedFolders)
                {
                    scopedLibraryIds.Add(collectionFolder.Id);
                }
            }
        }

        foreach (var folder in virtualFolders)
        {
            // If libraries have been selected for analysis, ensure this library was selected.
            if (folder.LibraryOptions?.DisabledMediaSegmentProviders?.Contains(plugin.Name) == true)
            {
                LogLibraryDisabled(_logger, folder.Name);
                continue;
            }

            // Some virtual folders don't have a proper item id.
            if (!Guid.TryParse(folder.ItemId, out var folderId))
            {
                _enumerationFailures++;
                LogInvalidFolderId(_logger, folder.Name);
                continue;
            }

            if (scopedLibraryIds is not null && !scopedLibraryIds.Contains(folderId))
            {
                LogSkippingLibraryNoRequestedItems(_logger, folder.Name);
                continue;
            }

            LogRunningEnqueueLibrary(_logger, folder.Name);

            try
            {
                await QueueLibraryContents(folderId, includeExcluded, seasonIds, policy, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _enumerationFailures++;
                LogFailedEnqueueLibrary(_logger, folder.Name, ex);
            }
        }

        if (_refreshedEpisodes.Count > 0)
        {
            LogRefreshedMetadata(_logger, _refreshedEpisodes.Count);
        }

        if (publishQueue)
        {
            plugin.QueuedMediaItems.Clear();
        }

        if (publishQueue || mergeQueue)
        {
            foreach (var kvp in _queuedEpisodes)
            {
                plugin.QueuedMediaItems[kvp.Key] = kvp.Value;
            }
        }

        return _queuedEpisodes;
    }

    private async Task QueueLibraryContents(
        Guid id,
        bool includeExcluded,
        IReadOnlyCollection<Guid>? seasonIds,
        ExclusionPolicy policy,
        CancellationToken cancellationToken)
    {
        var query = BuildLibraryQuery(id, BaseItemKind.Episode, BaseItemKind.Movie);

        var items = seasonIds is null
            ? _libraryManager.GetItemList(query, false)
            : GetScopedLibraryItems(id, seasonIds);

        if (items is null)
        {
            _enumerationFailures++;
            LogLibraryQueryNull(_logger);
            return;
        }

        // Dedupe both paths here: GetItemList has returned the same item twice, and the scoped
        // path concatenates overlapping queries. QueueEpisode appends unconditionally, and a
        // duplicate in a season pairs the episode against itself in the fingerprint pool.
        foreach (var item in items.DistinctBy(e => e.Id))
        {
            try
            {
                await QueueItem(item, includeExcluded, policy, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Count item-level failures too: a live item that failed to queue must not be
                // classified as stale by cleanup just because its siblings enumerated fine.
                _enumerationFailures++;
                LogErrorProcessingItem(_logger, ex, item.Name, item.Id);
            }
        }
    }

    private IEnumerable<BaseItem> GetScopedLibraryItems(Guid libraryId, IReadOnlyCollection<Guid> seasonIds)
    {
        Guid[] targetIds = [.. seasonIds];

        var episodeQuery = BuildLibraryQuery(libraryId, BaseItemKind.Episode);
        episodeQuery.AncestorIds = targetIds;

        var movieQuery = BuildLibraryQuery(libraryId, BaseItemKind.Movie);
        movieQuery.ItemIds = targetIds;

        // Match the unscoped path, which treats a null GetItemList result as an empty library.
        // In-season specials returned directly (a requested Specials season) are held back: they
        // belong to their AirsBefore/AirsAfter host season, and queueing them alongside that host
        // season's episodes would key them under the raw Specials id and compare them only
        // against other specials, diverging from the full-scan grouping. Those whose host season
        // is part of the request come back through the specials query below; the rest are restored
        // afterwards, since dropping them outright would leave a requested Specials season — the
        // id Entrypoint enqueues for a newly added special — with nothing to analyze.
        List<BaseItem> episodes = [];
        List<Episode> heldBackSpecials = [];
        foreach (var item in _libraryManager.GetItemList(episodeQuery, false) ?? [])
        {
            if (item is Episode episode && IsInSeasonSpecial(episode))
            {
                heldBackSpecials.Add(episode);
            }
            else
            {
                episodes.Add(item);
            }
        }

        var movies = _libraryManager.GetItemList(movieQuery, false) ?? [];

        // In-season specials are stored beneath their own raw SeasonId, but belong to the season
        // identified by AirsBefore/AirsAfterSeasonNumber. Query only specials from the series that
        // supplied the requested season episodes, then filter them to those resolved season keys.
        HashSet<(Guid SeriesId, int SeasonNumber)> targetSeasonKeys =
        [
            .. episodes
                .OfType<Episode>()
                .Where(episode => episode.SeriesId != Guid.Empty && episode.AiredSeasonNumber is not null)
                .Select(episode => (episode.SeriesId, episode.AiredSeasonNumber!.Value))
        ];

        IEnumerable<BaseItem> specials = [];
        if (targetSeasonKeys.Count > 0)
        {
            Guid[] seriesIds = [.. targetSeasonKeys.Select(key => key.SeriesId).Distinct()];
            var specialQuery = BuildLibraryQuery(libraryId, BaseItemKind.Episode);
            specialQuery.AncestorIds = seriesIds;
            specialQuery.ParentIndexNumber = 0;

            specials = (_libraryManager.GetItemList(specialQuery, false) ?? [])
                .Where(item => item is Episode episode &&
                    episode.AiredSeasonNumber is int airedSeasonNumber &&
                    targetSeasonKeys.Contains((episode.SeriesId, airedSeasonNumber)));
        }

        // Restore the held-back specials whose host season is not part of this request: nothing
        // above re-adds them, and GetSeasonId falls back to their raw Specials id, which is the
        // same grouping a full scan gives them when their host season queues no episodes.
        var restoredSpecials = heldBackSpecials
            .Where(episode => episode.AiredSeasonNumber is not int airedSeasonNumber ||
                !targetSeasonKeys.Contains((episode.SeriesId, airedSeasonNumber)));

        return episodes.Concat(specials).Concat(restoredSpecials).Concat(movies);
    }

    // The query shape shared by the unscoped and scoped paths; scoped callers add only their
    // scoping field (AncestorIds / ItemIds / ParentIndexNumber). The ordering keeps status
    // updates logged in series, season, episode order.
    private static InternalItemsQuery BuildLibraryQuery(Guid parentId, params BaseItemKind[] kinds) => new()
    {
        ParentId = parentId,
        OrderBy = [(ItemSortBy.SeriesSortName, SortOrder.Ascending), (ItemSortBy.ParentIndexNumber, SortOrder.Descending), (ItemSortBy.IndexNumber, SortOrder.Ascending)],
        IncludeItemTypes = kinds,
        Recursive = true,
        IsVirtualItem = false
    };

    // GetSeasonId's grouping rule: a special stored in Season 0 that airs within another season
    // belongs to that host season, not to the Specials season it is stored under.
    private static bool IsInSeasonSpecial(Episode episode)
        => episode.ParentIndexNumber == 0 && episode.AiredSeasonNumber != 0;

    private async Task QueueItem(BaseItem item, bool includeExcluded, ExclusionPolicy policy, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance!;
        var episode = item as Episode;

        if (string.IsNullOrEmpty(item.Path))
        {
            _enumerationFailures++;
            if (episode is null)
            {
                LogNotQueuingMovieNoPath(_logger, item.Name, item.Id);
            }
            else
            {
                LogNotQueuingEpisodeNoPath(_logger, episode.Name, episode.SeriesName, episode.Id);
            }

            return;
        }

        var decision = episode is null
            ? policy.EvaluateMovie(item.Name, item.Path)
            : policy.EvaluateSeries(episode.SeriesName, item.Path);
        if (decision.IsExcluded && !includeExcluded)
        {
            LogSkippingExcludedItem(_logger, item.Name, decision.RuleLabel);
            return;
        }

        // Movies are queued under their own id; episodes under the resolved season key.
        var seasonId = episode is null ? item.Id : await GetSeasonId(episode, cancellationToken).ConfigureAwait(false);
        if (!_queuedEpisodes.TryGetValue(seasonId, out var seasonEpisodes))
        {
            seasonEpisodes = [];
            _queuedEpisodes[seasonId] = seasonEpisodes;
            if (episode is not null)
            {
                _seasonKeys.TryAdd((episode.SeriesId, episode.AiredSeasonNumber ?? 0), seasonId);
            }
        }

        var config = plugin.Configuration;
        var duration = TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds;
        var creditsDuration = decision.IsExcluded
            ? duration
            : await ResolveCreditsFingerprintEndAsync(item.Path, duration, cancellationToken).ConfigureAwait(false);

        // Credits have their own maximum duration in seconds. The general analysis
        // percentage is not applied to them, since it can exclude the actual credits boundary.
        var maxCreditsDuration = episode is null ? config.MaximumMovieCreditsDuration : config.MaximumCreditsDuration;

        seasonEpisodes.Add(new QueuedEpisode
        {
            SeriesName = episode is null ? item.Name : episode.SeriesName,
            SeasonNumber = episode?.AiredSeasonNumber ?? 0,
            SeriesId = episode?.SeriesId ?? item.Id,
            // The resolved queue key, not the raw Jellyfin SeasonId: for in-season specials
            // (and unresolved SeasonIds) they differ, and season-state rows are keyed by the
            // resolved value everywhere downstream.
            SeasonId = seasonId,
            EpisodeNumber = episode?.IndexNumber ?? 0,
            EpisodeId = item.Id,
            Name = item.Name,
            Category = episode is null ? QueuedMediaCategory.Movie : ResolveEpisodeCategory(episode, seasonEpisodes, plugin),
            IsExcluded = decision.IsExcluded,
            Path = item.Path,
            Duration = duration,
            DateAdded = episode is null ? default : EpisodeAvailabilityDate(episode),
            IntroFingerprintEnd = episode is null
                ? 0
                : Math.Min(duration >= 5 * 60 ? duration * _analysisPercent : duration, 60 * config.AnalysisLengthLimit),
            CreditsFingerprintStart = Math.Max(0, creditsDuration - maxCreditsDuration),
            CreditsFingerprintEnd = creditsDuration,
        });
    }

    private static QueuedMediaCategory ResolveEpisodeCategory(Episode episode, IReadOnlyList<QueuedEpisode> seasonEpisodes, Plugin pluginInstance)
    {
        if (seasonEpisodes.FirstOrDefault()?.Category is QueuedMediaCategory cat && (cat == QueuedMediaCategory.AnimeEpisode || cat == QueuedMediaCategory.Episode))
        {
            return cat;
        }

        if (pluginInstance.GetItem(episode.SeriesId) is Series series &&
            SeriesHelper.IsAnime(series))
        {
            return QueuedMediaCategory.AnimeEpisode;
        }

        return QueuedMediaCategory.Episode;
    }

    internal static DateTime EpisodeAvailabilityDate(Episode episode)
    {
        return episode.DateCreated != default ? episode.DateCreated : episode.DateLastSaved;
    }

    private async Task<double> ResolveCreditsFingerprintEndAsync(string path, double duration, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance!.Configuration.ProbeAudioDuration)
        {
            return duration;
        }

        var audioDuration = await _ffmpegService.ProbeAudioDurationAsync(path, cancellationToken).ConfigureAwait(false);
        return audioDuration is > 0 && audioDuration.Value < duration
            ? audioDuration.Value
            : duration;
    }

    private async Task<Guid> GetSeasonId(Episode episode, CancellationToken cancellationToken)
    {
        if (IsInSeasonSpecial(episode) &&
            episode.AiredSeasonNumber is { } airedSeasonNumber &&
            _seasonKeys.TryGetValue((episode.SeriesId, airedSeasonNumber), out var hostSeasonId))
        {
            return hostSeasonId;
        }

        if (episode.SeasonId == Guid.Empty && episode.ParentIndexNumber is not null && !_refreshedEpisodes.Contains(episode.Id))
        {
            LogInvalidSeasonId(_logger, episode.Name, episode.Id);
            _refreshedEpisodes.Add(episode.Id);

            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.None,
                ReplaceAllImages = false,
                ReplaceAllMetadata = false,
                ForceSave = false,
                IsAutomated = false,
                RemoveOldMetadata = false,
                RegenerateTrickplay = false
            };

            await _providerManager.RefreshSingleItem(episode, refreshOptions, cancellationToken).ConfigureAwait(false);

            if (episode.SeasonId == Guid.Empty)
            {
                LogFailedResolveSeasonId(_logger, episode.Name, episode.Id);
                episode.SeasonId = episode.Id; // Use episode ID as fallback to avoid losing this episode entirely, it just won't be grouped with the rest of the season
            }
        }

        return episode.SeasonId;
    }

    /// <summary>
    /// Verify that a collection of queued media items still exist in Jellyfin and in storage.
    /// This is done to ensure that we don't analyze items that were deleted between the call to GetMediaInventoryAsync() and popping them from the queue.
    /// </summary>
    /// <param name="candidates">Queued media items.</param>
    /// <param name="modes">Analysis modes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Media items that have been verified to exist in Jellyfin and in storage.</returns>
    internal async Task<IReadOnlyList<QueuedEpisode>> VerifyQueueAsync(IReadOnlyList<QueuedEpisode> candidates, IReadOnlyCollection<AnalysisMode> modes, CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var verified = new List<QueuedEpisode>(candidates.Count);
        var plugin = Plugin.Instance!;
        // Built from the live configuration, not the inventory-time policy: exclusions saved
        // between the inventory and this verification must apply.
        var policy = ExclusionPolicy.FromConfiguration(plugin.Configuration);
        var ffmpegValid = await GetFfmpegValidAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await _database.GetSeasonQueueSnapshotAsync(candidates[0].SeasonId, [.. candidates.Select(c => c.EpisodeId)], cancellationToken).ConfigureAwait(false);
        var verifier = new QueueVerifier(plugin.Configuration, modes, snapshot, ffmpegValid);

        foreach (var candidate in candidates)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = plugin.GetItem(candidate.EpisodeId)?.Path;

                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    LogSkippingFileNotFound(_logger, candidate.Name, candidate.EpisodeId);
                    continue;
                }

                var decision = candidate.Category == QueuedMediaCategory.Movie
                    ? policy.EvaluateMovie(candidate.Name, path)
                    : policy.EvaluateSeries(candidate.SeriesName, path);
                if (decision.IsExcluded)
                {
                    LogSkippingExcludedItem(_logger, candidate.Name, decision.RuleLabel);
                    continue;
                }

                candidate.Path = path;
                verified.Add(candidate);
                verifier.Classify(candidate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogSkippingAnalysisException(_logger, candidate.Name, candidate.EpisodeId, ex);
            }
        }

        verifier.LogAnalysisReasons(_logger, verified);

        return verified;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Plugin instance is null in GetMediaInventoryAsync()")]
    private static partial void LogPluginInstanceNull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Library manager returned null when requesting virtual folders")]
    private static partial void LogLibraryManagerNull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping library \"{Name}\": virtual folder does not have a valid item id")]
    private static partial void LogInvalidFolderId(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Not analyzing library \"{Name}\": Intro Skipper is disabled in library settings. To enable, check library configuration > Media Segment Providers")]
    private static partial void LogLibraryDisabled(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping library {Name}: it contains none of the requested items")]
    private static partial void LogSkippingLibraryNoRequestedItems(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Requested item {ItemId} resolved to no library; querying every enabled library for this run")]
    private static partial void LogUnresolvedScopedItemLibrary(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Running enqueue of items in library {Name}")]
    private static partial void LogRunningEnqueueLibrary(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to enqueue items from library {Name}")]
    private static partial void LogFailedEnqueueLibrary(ILogger logger, string name, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed metadata for {Count} episodes with invalid SeasonIds")]
    private static partial void LogRefreshedMetadata(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Library query result is null")]
    private static partial void LogLibraryQueryNull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping excluded item {Name}: matched {RuleLabel}")]
    private static partial void LogSkippingExcludedItem(ILogger logger, string name, string ruleLabel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configured path exclusions include {Count} filesystem root or drive root entries")]
    private static partial void LogBroadPathRootExclusions(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing item {Name} ({Id})")]
    private static partial void LogErrorProcessingItem(ILogger logger, Exception ex, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not queuing episode \"{Name}\" from series \"{Series}\" ({Id}) as no path was provided by Jellyfin")]
    private static partial void LogNotQueuingEpisodeNoPath(ILogger logger, string name, string series, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not queuing movie \"{Name}\" ({Id}) as no path was provided by Jellyfin")]
    private static partial void LogNotQueuingMovieNoPath(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Episode {Name} ({Id}) has an invalid SeasonId")]
    private static partial void LogInvalidSeasonId(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to resolve SeasonId for episode {Name} ({Id}) after metadata refresh")]
    private static partial void LogFailedResolveSeasonId(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping {Name} ({Id}): file not found")]
    private static partial void LogSkippingFileNotFound(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping analysis of {Name} ({Id})")]
    private static partial void LogSkippingAnalysisException(ILogger logger, string name, Guid id, Exception exception);
}
