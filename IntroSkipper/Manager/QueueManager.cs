// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
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
public partial class QueueManager(ILogger<QueueManager> logger, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, IFFmpegService ffmpegService)
{
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProviderManager _providerManager = providerManager;
    private readonly ILogger<QueueManager> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly Dictionary<Guid, List<QueuedEpisode>> _queuedEpisodes = [];
    private readonly HashSet<Guid> _refreshedEpisodes = [];
    private double _analysisPercent;
    private HashSet<string> _excludedSeriesNames = [];
    private HashSet<string> _excludedMovieNames = [];
    private string[] _excludePaths = [];

    /// <summary>
    /// Gets all media items on the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Queued media items.</returns>
    public async Task<IReadOnlyDictionary<Guid, List<QueuedEpisode>>> GetMediaItems(CancellationToken cancellationToken = default)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            LogPluginInstanceNull(_logger);
            return _queuedEpisodes;
        }

        plugin.TotalQueued = 0;

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
                await QueueLibraryContents(folderId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogFailedEnqueueLibrary(_logger, folder.Name, ex);
            }
        }

        if (_refreshedEpisodes.Count > 0)
        {
            LogRefreshedMetadata(_logger, _refreshedEpisodes.Count);
        }

        plugin.TotalSeasons = _queuedEpisodes.Count;
        plugin.QueuedMediaItems.Clear();
        foreach (var kvp in _queuedEpisodes)
        {
            plugin.QueuedMediaItems.TryAdd(kvp.Key, kvp.Value);
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

        _excludedSeriesNames = CreateExcludedNameSet(config.ExcludeSeries);
        _excludedMovieNames = CreateExcludedNameSet(config.ExcludeMovies);
        _excludePaths = SplitConfiguredList(config.ExcludePaths);

        // If analysis settings have been changed from the default, log the modified settings.
        if (config.AnalysisLengthLimit != PluginConfiguration.DefaultAnalysisLengthLimit
            || config.AnalysisPercent != PluginConfiguration.DefaultAnalysisPercent
            || config.MinimumIntroDuration != PluginConfiguration.DefaultMinimumIntroDuration)
        {
            LogAnalysisSettingsChanged(_logger, config.AnalysisPercent, config.AnalysisLengthLimit, config.MinimumIntroDuration);
        }
    }

    private bool ShouldSkipEpisode(Episode episode)
    {
        if (IsSeriesExcluded(episode.SeriesName))
        {
            LogSkippingExcludedSeries(_logger, episode.SeriesName);
            return true;
        }

        if (IsPathExcluded(episode.Path))
        {
            LogSkippingExcludedPath(_logger, Path.GetFileName(episode.Path));
            return true;
        }

        return false;
    }

    private bool ShouldSkipMovie(Movie movie)
    {
        if (IsMovieExcluded(movie.Name))
        {
            LogSkippingExcludedMovie(_logger, movie.Name);
            return true;
        }

        if (IsPathExcluded(movie.Path))
        {
            LogSkippingExcludedPath(_logger, Path.GetFileName(movie.Path));
            return true;
        }

        return false;
    }

    private async Task QueueLibraryContents(Guid id, CancellationToken cancellationToken)
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

        var items = _libraryManager.GetItemList(query, false);
        if (items is null)
        {
            LogLibraryQueryNull(_logger);
            return;
        }

        // Queue all supported library items on the server for analysis.
        LogIteratingLibraryItems(_logger);

        var queuedCount = 0;
        foreach (var item in items.DistinctBy(e => e.Id))
        {
            try
            {
                if (item is Episode episode)
                {
                    if (!ShouldSkipEpisode(episode))
                    {
                        await QueueEpisode(episode, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (item is Movie movie)
                {
                    if (!ShouldSkipMovie(movie))
                    {
                        await QueueMovieAsync(movie, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    LogItemNotEpisodeOrMovie(_logger, item.Name);
                }

                queuedCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogErrorProcessingItem(_logger, ex, item.Name, item.Id);
            }
        }

        LogQueuedEpisodes(_logger, queuedCount);
    }

    /// <summary>
    /// Normalizes a media name by removing punctuation and whitespace
    /// and converting to lowercase to make comparisons more robust.
    /// </summary>
    /// <param name="name">The media name to normalize.</param>
    /// <returns>Normalized media name.</returns>
    private static string NormalizeExcludedName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var length = 0;
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                length++;
            }
        }

        return string.Create(length, name, static (destination, source) =>
            {
                var index = 0;
                foreach (var ch in source)
                {
                    if (char.IsLetterOrDigit(ch))
                    {
                        destination[index++] = char.ToLowerInvariant(ch);
                    }
                }
            });
    }

    /// <summary>
    /// Checks if a media name is in the excluded list, using normalized name comparison
    /// to handle differences in punctuation and spacing.
    /// </summary>
    /// <param name="name">The media name to check.</param>
    /// <param name="excludedNames">The configured normalized names to exclude.</param>
    /// <returns>True if the media item should be excluded, false otherwise.</returns>
    internal static bool IsNameExcluded(string name, IReadOnlySet<string> excludedNames)
    {
        return !string.IsNullOrEmpty(name) &&
               excludedNames.Count != 0 &&
               excludedNames.Contains(NormalizeExcludedName(name));
    }

    internal static HashSet<string> CreateExcludedNameSet(string excludedNames)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in SplitConfiguredList(excludedNames))
        {
            var normalized = NormalizeExcludedName(name);
            if (normalized.Length != 0)
            {
                set.Add(normalized);
            }
        }

        return set;
    }

    private bool IsSeriesExcluded(string seriesName) => IsNameExcluded(seriesName, _excludedSeriesNames);

    private bool IsMovieExcluded(string movieName) => IsNameExcluded(movieName, _excludedMovieNames);

    private static string[] SplitConfiguredList(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Checks if a media item's file path matches any of the configured exclusion fragments.
    /// </summary>
    /// <param name="path">The full media file path to check.</param>
    /// <returns>True if the path should be excluded, false otherwise.</returns>
    private bool IsPathExcluded(string path) => IsPathExcluded(path, _excludePaths);

    /// <summary>
    /// Checks if a media item's file path contains any of the provided exclusion fragments,
    /// using a case-insensitive substring comparison.
    /// </summary>
    /// <param name="path">The full media file path to check.</param>
    /// <param name="excludePaths">The configured path fragments to exclude.</param>
    /// <returns>True if the path should be excluded, false otherwise.</returns>
    internal static bool IsPathExcluded(string path, IReadOnlyCollection<string> excludePaths)
    {
        if (string.IsNullOrEmpty(path) || excludePaths.Count == 0)
        {
            return false;
        }

        foreach (var fragment in excludePaths)
        {
            if (!string.IsNullOrEmpty(fragment) && path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task QueueEpisode(Episode episode, CancellationToken cancellationToken)
    {
        var pluginInstance = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance was null");

        if (string.IsNullOrEmpty(episode.Path))
        {
            LogNotQueuingEpisodeNoPath(_logger, episode.Name, episode.SeriesName, episode.Id);
            return;
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

        var creditsDuration = await ResolveCreditsFingerprintEndAsync(episode.Path, duration, cancellationToken).ConfigureAwait(false);

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
            Path = episode.Path,
            Duration = duration,
            DateAdded = EpisodeAvailabilityDate(episode),
            IntroFingerprintEnd = fingerprintDuration,
            CreditsFingerprintStart = Math.Max(0, creditsDuration - maxCreditsDuration),
            CreditsFingerprintEnd = creditsDuration,
        });

        pluginInstance.TotalQueued++;
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

    private async Task QueueMovieAsync(Movie movie, CancellationToken cancellationToken)
    {
        var pluginInstance = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance was null");

        if (string.IsNullOrEmpty(movie.Path))
        {
            LogNotQueuingMovieNoPath(_logger, movie.Name, movie.Id);
            return;
        }

        // Allocate a new list for each movie.
        _queuedEpisodes.TryAdd(movie.Id, []);

        var duration = TimeSpan.FromTicks(movie.RunTimeTicks ?? 0).TotalSeconds;
        var creditsDuration = await ResolveCreditsFingerprintEndAsync(movie.Path, duration, cancellationToken).ConfigureAwait(false);

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
        });

        pluginInstance.TotalQueued++;
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
    /// <param name="seasonId">Season ID for the candidate group.</param>
    /// <param name="candidates">Queued media items.</param>
    /// <param name="modes">Analysis modes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Media items that have been verified to exist in Jellyfin and in storage.</returns>
    internal async Task<QueueVerificationResult> VerifyQueueAsync(Guid seasonId, IReadOnlyList<QueuedEpisode> candidates, IReadOnlyCollection<AnalysisMode> modes, CancellationToken cancellationToken = default)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return new QueueVerificationResult([], 0);
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is null");
        var verification = VerifyExistingMediaFiles(plugin, candidates, cancellationToken);
        var verified = verification.Episodes;
        if (verified.Count == 0 || modes == null || modes.Count == 0)
        {
            return verification;
        }

        var episodeIds = new Guid[verified.Count];
        for (var i = 0; i < verified.Count; i++)
        {
            episodeIds[i] = verified[i].EpisodeId;
        }

        var snapshot = await Plugin.GetSeasonQueueSnapshotAsync(seasonId, episodeIds, cancellationToken).ConfigureAwait(false);
        List<(AnalysisMode Mode, bool HashMatches)> modeStates;
        try
        {
            modeStates = CreateQueueAnalysisModeStates(modes, plugin, snapshot);
        }
        catch (Exception ex)
        {
            foreach (var candidate in verified)
            {
                LogSkippingAnalysisException(_logger, candidate.Name, candidate.EpisodeId, ex);
            }

            return verification;
        }

        foreach (var candidate in verified)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyStoredAnalysisState(candidate, modeStates, plugin.AnalyzeAgain, snapshot);
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

        return verification;
    }

    private QueueVerificationResult VerifyExistingMediaFiles(Plugin plugin, IReadOnlyList<QueuedEpisode> candidates, CancellationToken cancellationToken)
    {
        var verified = new List<QueuedEpisode>(candidates.Count);
        var skipped = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetVerifiedPath(plugin, candidate, out var path))
                {
                    skipped++;
                    continue;
                }

                candidate.Path = path;
                verified.Add(candidate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                skipped++;
                LogSkippingAnalysisException(_logger, candidate.Name, candidate.EpisodeId, ex);
            }
        }

        return new QueueVerificationResult(verified, skipped);
    }

    private bool TryGetVerifiedPath(Plugin plugin, QueuedEpisode candidate, out string path)
    {
        path = string.Empty;
        if (candidate.Category is QueuedMediaCategory.Movie)
        {
            if (IsMovieExcluded(candidate.Name))
            {
                LogSkippingExcludedMovie(_logger, candidate.Name);
                return false;
            }
        }
        else if (IsSeriesExcluded(candidate.SeriesName))
        {
            LogSkippingExcludedSeries(_logger, candidate.SeriesName);
            return false;
        }

        path = plugin.GetItemPath(candidate.EpisodeId);
        if (string.IsNullOrEmpty(path))
        {
            LogSkippingFileNotFound(_logger, candidate.Name, candidate.EpisodeId);
            return false;
        }

        if (IsPathExcluded(path))
        {
            LogSkippingExcludedPath(_logger, Path.GetFileName(path));
            return false;
        }

        if (!File.Exists(path))
        {
            LogSkippingFileNotFound(_logger, candidate.Name, candidate.EpisodeId);
            return false;
        }

        return true;
    }

    private static List<(AnalysisMode Mode, bool HashMatches)> CreateQueueAnalysisModeStates(IReadOnlyCollection<AnalysisMode> modes, Plugin plugin, SeasonQueueSnapshot snapshot)
    {
        var modeStates = new List<(AnalysisMode Mode, bool HashMatches)>(modes.Count);
        foreach (var mode in modes)
        {
            var action = snapshot.AnalyzerActionByMode.TryGetValue(mode, out var savedAction)
                ? savedAction
                : AnalyzerAction.Default;
            var expectedHash = ConfigHasher.Analysis(plugin.Configuration, mode, action);
            var hashMatches = snapshot.ConfigHashByMode.TryGetValue(mode, out var savedHash) &&
                string.Equals(savedHash, expectedHash, StringComparison.Ordinal);

            modeStates.Add((mode, hashMatches));
        }

        return modeStates;
    }

    private static void ApplyStoredAnalysisState(QueuedEpisode candidate, IReadOnlyList<(AnalysisMode Mode, bool HashMatches)> modeStates, bool analyzeAgain, SeasonQueueSnapshot snapshot)
    {
        snapshot.SegmentsByEpisodeId.TryGetValue(candidate.EpisodeId, out var segmentsByMode);

        foreach (var state in modeStates)
        {
            if (segmentsByMode is not null && segmentsByMode.TryGetValue(state.Mode, out _))
            {
                var isUserProvided = snapshot.UserProvidedByMode.TryGetValue(state.Mode, out var userProvided) &&
                                     userProvided.Contains(candidate.EpisodeId);

                // Always preserve user-provided segments. When AnalyzeAgain is true (settings
                // changed), leave automatically-analyzed segments as NotAnalyzed so they are
                // re-analyzed and their timestamps updated to reflect the new settings.
                if (isUserProvided || (!analyzeAgain && state.HashMatches))
                {
                    candidate.SetAnalyzed(state.Mode, isUserProvided ? EpisodeState.UserProvided : EpisodeState.Analyzed);
                }
            }
            else if (!analyzeAgain && state.HashMatches &&
                     snapshot.EpisodeIdsByMode.TryGetValue(state.Mode, out var ids) &&
                     ids.Contains(candidate.EpisodeId))
            {
                candidate.SetAnalyzed(state.Mode, EpisodeState.NoSegments);
            }
        }
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping excluded series: {Series}")]
    private static partial void LogSkippingExcludedSeries(ILogger logger, string series);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping excluded movie: {Movie}")]
    private static partial void LogSkippingExcludedMovie(ILogger logger, string movie);

    // Log only the file name (not the full path) to avoid exposing user-specific directory structures in shareable logs.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping excluded path for file {File}")]
    private static partial void LogSkippingExcludedPath(ILogger logger, string file);

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
