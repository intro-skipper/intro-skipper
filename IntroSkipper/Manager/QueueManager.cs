// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
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
public partial class QueueManager(ILogger<QueueManager> logger, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, IFFmpegService ffmpegService, IIntroSkipperDatabase database)
{
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProviderManager _providerManager = providerManager;
    private readonly ILogger<QueueManager> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly Dictionary<Guid, List<QueuedEpisode>> _queuedEpisodes = [];
    private readonly HashSet<Guid> _refreshedEpisodes = [];
    private bool? _ffmpegValid;
    private double _analysisPercent;
    private int _enumerationFailures;
    private ExclusionPolicy _exclusionPolicy = ExclusionPolicy.Empty;

    /// <summary>
    /// Gets the number of libraries or individual items that failed to enumerate or queue
    /// during the most recent <see cref="GetMediaItems(bool, CancellationToken)"/> call.
    /// A non-zero value means the queue is incomplete; callers that delete data missing from
    /// the queue must not treat the affected items as stale.
    /// </summary>
    internal int EnumerationFailureCount => _enumerationFailures;

    /// <summary>
    /// Gets all media items on the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Queued media items.</returns>
    public Task<IReadOnlyDictionary<Guid, List<QueuedEpisode>>> GetMediaItems(CancellationToken cancellationToken = default)
        => GetMediaItems(includeExcluded: false, cancellationToken);

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

    internal async Task<IReadOnlyDictionary<Guid, List<QueuedEpisode>>> GetMediaItems(bool includeExcluded, CancellationToken cancellationToken = default)
    {
        _enumerationFailures = 0;

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            LogPluginInstanceNull(_logger);
            return _queuedEpisodes;
        }

        if (!includeExcluded)
        {
            plugin.TotalQueued = 0;
        }

        LoadAnalysisSettings(plugin);

        // For all selected libraries, enqueue all contained episodes.
        var virtualFolders = _libraryManager.GetVirtualFolders();
        if (virtualFolders is null)
        {
            LogLibraryManagerNull(_logger);
            return _queuedEpisodes;
        }

        foreach (var folder in virtualFolders)
        {
            // If libraries have been selected for analysis, ensure this library was selected.
            if (folder.LibraryOptions?.DisabledMediaSegmentProviders?.Contains(plugin.Name) == true)
            {
                LogLibraryDisabled(_logger, folder.Name);
                continue;
            }

            LogRunningEnqueueLibrary(_logger, folder.Name);

            // Some virtual folders don't have a proper item id.
            if (!Guid.TryParse(folder.ItemId, out var folderId))
            {
                continue;
            }

            try
            {
                await QueueLibraryContents(folderId, includeExcluded, cancellationToken).ConfigureAwait(false);
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

        if (!includeExcluded)
        {
            plugin.TotalSeasons = _queuedEpisodes.Count;
            plugin.QueuedMediaItems.Clear();
            foreach (var kvp in _queuedEpisodes)
            {
                plugin.QueuedMediaItems.TryAdd(kvp.Key, kvp.Value);
            }
        }

        return _queuedEpisodes;
    }

    /// <summary>
    /// Loads the list of libraries which have been selected for analysis and the minimum intro duration.
    /// Settings which have been modified from the defaults are logged.
    /// </summary>
    private void LoadAnalysisSettings(Plugin plugin)
    {
        var config = plugin.Configuration;

        // Store the analysis percent
        _analysisPercent = Convert.ToDouble(config.AnalysisPercent) / 100;

        _exclusionPolicy = ExclusionPolicy.FromConfiguration(config);
        if (_exclusionPolicy.BroadPathRootCount > 0)
        {
            LogBroadPathRootExclusions(_logger, _exclusionPolicy.BroadPathRootCount);
        }

        // If analysis settings have been changed from the default, log the modified settings.
        if (config.AnalysisLengthLimit != PluginConfiguration.DefaultAnalysisLengthLimit
            || config.AnalysisPercent != PluginConfiguration.DefaultAnalysisPercent
            || config.MinimumIntroDuration != PluginConfiguration.DefaultMinimumIntroDuration)
        {
            LogAnalysisSettingsChanged(_logger, config.AnalysisPercent, config.AnalysisLengthLimit, config.MinimumIntroDuration);
        }
    }

    private async Task QueueLibraryContents(Guid id, bool includeExcluded, CancellationToken cancellationToken)
    {
        LogConstructingQuery(_logger);

        var query = new InternalItemsQuery
        {
            // Order by series name, season, and then episode number so that status updates are logged in order
            ParentId = id,
            OrderBy = [(ItemSortBy.SeriesSortName, SortOrder.Ascending), (ItemSortBy.ParentIndexNumber, SortOrder.Descending), (ItemSortBy.IndexNumber, SortOrder.Ascending),],
            IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false
        };

        var items = _libraryManager.GetItemList(query, false)
            .DistinctBy(e => e.Id)
            .ToList();

        if (items is null)
        {
            LogLibraryQueryNull(_logger);
            return;
        }

        // Queue all supported library items on the server for analysis.
        LogIteratingLibraryItems(_logger);

        var queuedCount = 0;
        foreach (var item in items)
        {
            try
            {
                if (item is Episode episode)
                {
                    if (await QueueEpisode(episode, includeExcluded, cancellationToken).ConfigureAwait(false))
                    {
                        queuedCount++;
                    }
                }
                else if (item is Movie movie)
                {
                    if (await QueueMovieAsync(movie, includeExcluded, cancellationToken).ConfigureAwait(false))
                    {
                        queuedCount++;
                    }
                }
                else
                {
                    LogItemNotEpisodeOrMovie(_logger, item.Name);
                }
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

        LogQueuedEpisodes(_logger, queuedCount);
    }

    private async Task<bool> QueueEpisode(Episode episode, bool includeExcluded, CancellationToken cancellationToken)
    {
        var pluginInstance = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance was null");

        if (string.IsNullOrEmpty(episode.Path))
        {
            LogNotQueuingEpisodeNoPath(_logger, episode.Name, episode.SeriesName, episode.Id);
            return false;
        }

        var decision = _exclusionPolicy.EvaluateSeries(episode.SeriesName, episode.SeriesId, episode.Path);
        if (decision.IsExcluded && !includeExcluded)
        {
            LogSkippingExcludedItem(_logger, episode.Name, decision.RuleLabel);
            return false;
        }

        // Allocate a new list for each new season
        var seasonId = await GetSeasonId(episode, cancellationToken).ConfigureAwait(false);

        if (!_queuedEpisodes.TryGetValue(seasonId, out var seasonEpisodes))
        {
            seasonEpisodes = [];
            _queuedEpisodes[seasonId] = seasonEpisodes;
        }

        var duration = TimeSpan.FromTicks(episode.RunTimeTicks ?? 0).TotalSeconds;
        var fingerprintDuration = Math.Min(
            duration >= 5 * 60 ? duration * _analysisPercent : duration,
            60 * pluginInstance.Configuration.AnalysisLengthLimit);

        var creditsDuration = decision.IsExcluded
            ? duration
            : await ResolveCreditsFingerprintEndAsync(episode.Path, duration, cancellationToken).ConfigureAwait(false);

        var maxCreditsDuration = Math.Min(
            creditsDuration >= 5 * 60 ? creditsDuration * _analysisPercent : creditsDuration,
            60 * pluginInstance.Configuration.MaximumCreditsDuration);

        // Queue the episode for analysis.
        seasonEpisodes.Add(new QueuedEpisode
        {
            SeriesName = episode.SeriesName,
            SeasonNumber = episode.AiredSeasonNumber ?? 0,
            SeriesId = episode.SeriesId,
            SeasonId = episode.SeasonId,
            EpisodeNumber = episode.IndexNumber ?? 0,
            EpisodeId = episode.Id,
            Name = episode.Name,
            Category = ResolveEpisodeCategory(episode, seasonEpisodes, pluginInstance),
            IsExcluded = decision.IsExcluded,
            Path = episode.Path,
            Duration = duration,
            DateAdded = EpisodeAvailabilityDate(episode),
            IntroFingerprintEnd = fingerprintDuration,
            CreditsFingerprintStart = Math.Max(0, creditsDuration - maxCreditsDuration),
            CreditsFingerprintEnd = creditsDuration,
        });

        if (!includeExcluded)
        {
            pluginInstance.TotalQueued++;
        }

        return true;
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

    private async Task<bool> QueueMovieAsync(Movie movie, bool includeExcluded, CancellationToken cancellationToken)
    {
        var pluginInstance = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance was null");

        if (string.IsNullOrEmpty(movie.Path))
        {
            LogNotQueuingMovieNoPath(_logger, movie.Name, movie.Id);
            return false;
        }

        var decision = _exclusionPolicy.EvaluateMovie(movie.Name, movie.Id, movie.Path);
        if (decision.IsExcluded && !includeExcluded)
        {
            LogSkippingExcludedItem(_logger, movie.Name, decision.RuleLabel);
            return false;
        }

        // Allocate a new list for each movie.
        _queuedEpisodes.TryAdd(movie.Id, []);

        var duration = TimeSpan.FromTicks(movie.RunTimeTicks ?? 0).TotalSeconds;
        var creditsDuration = decision.IsExcluded
            ? duration
            : await ResolveCreditsFingerprintEndAsync(movie.Path, duration, cancellationToken).ConfigureAwait(false);

        _queuedEpisodes[movie.Id].Add(new QueuedEpisode
        {
            SeriesName = movie.Name,
            SeriesId = movie.Id,
            SeasonId = movie.Id,
            EpisodeId = movie.Id,
            Name = movie.Name,
            Path = movie.Path,
            Duration = duration,
            CreditsFingerprintStart = Math.Max(0, creditsDuration - pluginInstance.Configuration.MaximumMovieCreditsDuration),
            CreditsFingerprintEnd = creditsDuration,
            Category = QueuedMediaCategory.Movie,
            IsExcluded = decision.IsExcluded,
        });

        if (!includeExcluded)
        {
            pluginInstance.TotalQueued++;
        }

        return true;
    }

    private async Task<double> ResolveCreditsFingerprintEndAsync(string path, double duration, CancellationToken cancellationToken)
    {
        var pluginInstance = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance was null");
        if (!pluginInstance.Configuration.ProbeAudioDuration)
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
        if (episode.ParentIndexNumber == 0 && episode.AiredSeasonNumber != 0) // In-season special
        {
            foreach (var kvp in _queuedEpisodes)
            {
                var first = kvp.Value.FirstOrDefault();
                if (first?.SeriesId == episode.SeriesId &&
                    first.SeasonNumber == episode.AiredSeasonNumber)
                {
                    return kvp.Key;
                }
            }
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
            else
            {
                LogResolvedSeasonId(_logger, episode.SeasonId, episode.Name, episode.Id);
            }
        }

        return episode.SeasonId;
    }

    /// <summary>
    /// Verify that a collection of queued media items still exist in Jellyfin and in storage.
    /// This is done to ensure that we don't analyze items that were deleted between the call to GetMediaItems() and popping them from the queue.
    /// </summary>
    /// <param name="candidates">Queued media items.</param>
    /// <param name="modes">Analysis modes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Media items that have been verified to exist in Jellyfin and in storage.</returns>
    internal async Task<IReadOnlyList<QueuedEpisode>> VerifyQueueAsync(IReadOnlyList<QueuedEpisode> candidates, IReadOnlyCollection<AnalysisMode> modes, CancellationToken cancellationToken = default)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return [];
        }

        var verified = new List<QueuedEpisode>(candidates.Count);
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is null");
        var policy = ExclusionPolicy.FromConfiguration(plugin.Configuration);
        var ffmpegValid = await GetFfmpegValidAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await _database.GetSeasonQueueSnapshotAsync(candidates[0].SeasonId, [.. candidates.Select(c => c.EpisodeId)], cancellationToken).ConfigureAwait(false);

        // The expected config hash depends on the season-level analyzer action and mode, not on the
        // individual episode, so compute it once per mode instead of once per episode and mode.
        var hashMatchesByMode = new Dictionary<AnalysisMode, bool>(modes.Count);
        foreach (var mode in modes)
        {
            var action = snapshot.AnalyzerActionByMode.TryGetValue(mode, out var savedAction)
                ? savedAction
                : AnalyzerAction.Default;
            var expectedHash = ConfigHasher.Analysis(plugin.Configuration, mode, action, ffmpegValid);
            hashMatchesByMode[mode] = snapshot.ConfigHashByMode.TryGetValue(mode, out var savedHash) &&
                string.Equals(savedHash, expectedHash, StringComparison.Ordinal);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = plugin.GetItemPath(candidate.EpisodeId);

                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    LogSkippingFileNotFound(_logger, candidate.Name, candidate.EpisodeId);
                    continue;
                }

                var decision = candidate.Category == QueuedMediaCategory.Movie
                    ? policy.EvaluateMovie(candidate.Name, candidate.EpisodeId, path)
                    : policy.EvaluateSeries(candidate.SeriesName, candidate.SeriesId, path);
                if (decision.IsExcluded)
                {
                    LogSkippingExcludedItem(_logger, candidate.Name, decision.RuleLabel);
                    continue;
                }

                candidate.Path = path;
                verified.Add(candidate);

                foreach (var mode in modes)
                {
                    var hashMatches = hashMatchesByMode[mode];

                    if (snapshot.SegmentsByEpisodeId.TryGetValue(candidate.EpisodeId, out var hasSegments) &&
                        hasSegments.TryGetValue(mode, out _))
                    {
                        var isUserProvided = snapshot.UserProvidedByMode.TryGetValue(mode, out var userProvided) &&
                                             userProvided.Contains(candidate.EpisodeId);

                        // Always preserve user-provided segments. When AnalyzeAgain is true (settings
                        // changed), leave automatically-analyzed segments as NotAnalyzed so they are
                        // re-analyzed and their timestamps updated to reflect the new settings.
                        if (isUserProvided || (!plugin.AnalyzeAgain && hashMatches))
                        {
                            candidate.SetAnalyzed(mode, isUserProvided ? EpisodeState.UserProvided : EpisodeState.Analyzed);
                        }
                    }
                    else if (!plugin.AnalyzeAgain && hashMatches &&
                             snapshot.EpisodeIdsByMode.TryGetValue(mode, out var ids) &&
                             ids.Contains(candidate.EpisodeId))
                    {
                        candidate.SetAnalyzed(mode, EpisodeState.NoSegments);
                    }
                }
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

        return verified;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Plugin instance is null in GetMediaItems()")]
    private static partial void LogPluginInstanceNull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Library manager returned null when requesting virtual folders")]
    private static partial void LogLibraryManagerNull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Not analyzing library \"{Name}\": Intro Skipper is disabled in library settings. To enable, check library configuration > Media Segment Providers")]
    private static partial void LogLibraryDisabled(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Running enqueue of items in library {Name}")]
    private static partial void LogRunningEnqueueLibrary(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to enqueue items from library {Name}")]
    private static partial void LogFailedEnqueueLibrary(ILogger logger, string name, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed metadata for {Count} episodes with invalid SeasonIds")]
    private static partial void LogRefreshedMetadata(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis settings have been changed to: {Percent}% / {Minutes}m and a minimum of {Minimum}s")]
    private static partial void LogAnalysisSettingsChanged(ILogger logger, int percent, int minutes, int minimum);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing anonymous internal query")]
    private static partial void LogConstructingQuery(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Library query result is null")]
    private static partial void LogLibraryQueryNull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Iterating through library items")]
    private static partial void LogIteratingLibraryItems(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping excluded item {Name}: matched {RuleLabel}")]
    private static partial void LogSkippingExcludedItem(ILogger logger, string name, string ruleLabel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configured path exclusions include {Count} filesystem root or drive root entries")]
    private static partial void LogBroadPathRootExclusions(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Item {Name} is not an episode or movie")]
    private static partial void LogItemNotEpisodeOrMovie(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing item {Name} ({Id})")]
    private static partial void LogErrorProcessingItem(ILogger logger, Exception ex, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Queued {Count} episodes")]
    private static partial void LogQueuedEpisodes(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not queuing episode \"{Name}\" from series \"{Series}\" ({Id}) as no path was provided by Jellyfin")]
    private static partial void LogNotQueuingEpisodeNoPath(ILogger logger, string name, string series, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not queuing movie \"{Name}\" ({Id}) as no path was provided by Jellyfin")]
    private static partial void LogNotQueuingMovieNoPath(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Episode {Name} ({Id}) has an invalid SeasonId")]
    private static partial void LogInvalidSeasonId(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to resolve SeasonId for episode {Name} ({Id}) after metadata refresh")]
    private static partial void LogFailedResolveSeasonId(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Successfully resolved SeasonId {SeasonId} for episode {Name} ({Id})")]
    private static partial void LogResolvedSeasonId(ILogger logger, Guid seasonId, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping {Name} ({Id}): file not found")]
    private static partial void LogSkippingFileNotFound(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping analysis of {Name} ({Id})")]
    private static partial void LogSkippingAnalysisException(ILogger logger, string name, Guid id, Exception exception);
}
