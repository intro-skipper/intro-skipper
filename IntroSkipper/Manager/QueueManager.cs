// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
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
public partial class QueueManager(ILogger<QueueManager> logger, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
{
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProviderManager _providerManager = providerManager;
    private readonly ILogger<QueueManager> _logger = logger;
    private readonly Dictionary<Guid, List<QueuedEpisode>> _queuedEpisodes = [];
    private readonly HashSet<Guid> _refreshedEpisodes = [];
    private double _analysisPercent;
    private List<string> _excludeSeries = [];

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
            _logger.LogError("Plugin instance is null in GetMediaItems()");
            return _queuedEpisodes;
        }

        plugin.TotalQueued = 0;

        LoadAnalysisSettings(plugin);

        // For all selected libraries, enqueue all contained episodes.
        var virtualFolders = _libraryManager.GetVirtualFolders();
        if (virtualFolders is null)
        {
            _logger.LogError("Library manager returned null when requesting virtual folders");
            return _queuedEpisodes;
        }

        foreach (var folder in virtualFolders)
        {
            // If libraries have been selected for analysis, ensure this library was selected.
            if (folder.LibraryOptions?.DisabledMediaSegmentProviders?.Contains(plugin.Name) == true && !plugin.PluginSkip)
            {
                _logger.LogDebug("Not analyzing library \"{Name}\": Intro Skipper is disabled in library settings. To enable, check library configuration > Media Segment Providers", folder.Name);
                continue;
            }

            _logger.LogInformation("Running enqueue of items in library {Name}", folder.Name);

            // Some virtual folders don't have a proper item id.
            if (!Guid.TryParse(folder.ItemId, out var folderId))
            {
                continue;
            }

            try
            {
                await QueueLibraryContents(folderId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to enqueue items from library {Name}: {Exception}", folder.Name, ex);
            }
        }

        if (_refreshedEpisodes.Count > 0)
        {
            _logger.LogInformation("Refreshed metadata for {Count} episodes with invalid SeasonIds", _refreshedEpisodes.Count);
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

        _excludeSeries = [.. config.ExcludeSeries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        // If analysis settings have been changed from the default, log the modified settings.
        if (config.AnalysisLengthLimit != 10 || config.AnalysisPercent != 25 || config.MinimumIntroDuration != 15)
        {
            _logger.LogInformation(
                "Analysis settings have been changed to: {Percent}% / {Minutes}m and a minimum of {Minimum}s",
                config.AnalysisPercent,
                config.AnalysisLengthLimit,
                config.MinimumIntroDuration);
        }
    }

    private async Task QueueLibraryContents(Guid id, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Constructing anonymous internal query");

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
            _logger.LogError("Library query result is null");
            return;
        }

        // Queue all episodes on the server for fingerprinting.
        _logger.LogDebug("Iterating through library items");

        foreach (var item in items)
        {
            try
            {
                if (item is Episode episode)
                {
                    if (!IsSeriesExcluded(episode.SeriesName))
                    {
                        await QueueEpisode(episode, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogDebug("Skipping excluded series: {Series}", episode.SeriesName);
                    }
                }
                else if (item is Movie movie)
                {
                    QueueMovie(movie);
                }
                else
                {
                    _logger.LogDebug("Item {Name} is not an episode or movie", item.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing item {Name} ({Id})", item.Name, item.Id);
            }
        }

        _logger.LogDebug("Queued {Count} episodes", items.Count);
    }

    /// <summary>
    /// Normalizes a series name by removing punctuation and converting to lowercase
    /// to make comparisons more robust.
    /// </summary>
    /// <param name="name">The series name to normalize.</param>
    /// <returns>Normalized series name.</returns>
    private static string NormalizeSeriesName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        // Remove all punctuation and convert to lowercase
        return NormalizeSeriesNameRegex().Replace(name, string.Empty)
            .ToLowerInvariant()
            .Trim();
    }

    /// <summary>
    /// Checks if a series is in the excluded list, using normalized name comparison
    /// to handle differences in punctuation.
    /// </summary>
    /// <param name="seriesName">The series name to check.</param>
    /// <returns>True if the series should be excluded, false otherwise.</returns>
    private bool IsSeriesExcluded(string seriesName)
    {
        if (string.IsNullOrEmpty(seriesName))
        {
            return false;
        }

        // Then check normalized match
        var normalizedName = NormalizeSeriesName(seriesName);
        return _excludeSeries.Contains(normalizedName);
    }

    private async Task QueueEpisode(Episode episode, CancellationToken cancellationToken)
    {
        var pluginInstance = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance was null");

        if (string.IsNullOrEmpty(episode.Path))
        {
            _logger.LogWarning(
                "Not queuing episode \"{Name}\" from series \"{Series}\" ({Id}) as no path was provided by Jellyfin",
                episode.Name,
                episode.SeriesName,
                episode.Id);
            return;
        }

        // Allocate a new list for each new season
        var seasonId = await GetSeasonId(episode, cancellationToken).ConfigureAwait(false);
        if (seasonId == Guid.Empty)
        {
            _logger.LogWarning(
                "Not queuing episode \"{Name}\" from series \"{Series}\" ({Id}) as SeasonId could not be resolved",
                episode.Name,
                episode.SeriesName,
                episode.Id);
            return;
        }

        if (!_queuedEpisodes.TryGetValue(seasonId, out var seasonEpisodes))
        {
            seasonEpisodes = [];
            _queuedEpisodes[seasonId] = seasonEpisodes;
        }

        var duration = TimeSpan.FromTicks(episode.RunTimeTicks ?? 0).TotalSeconds;
        var fingerprintDuration = Math.Min(
            duration >= 5 * 60 ? duration * _analysisPercent : duration,
            60 * pluginInstance.Configuration.AnalysisLengthLimit);

        var maxCreditsDuration = Math.Min(
            duration >= 5 * 60 ? duration * _analysisPercent : duration,
            60 * pluginInstance.Configuration.MaximumCreditsDuration);

        // Queue the episode for analysis
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
            IsExcluded = IsSeriesExcluded(episode.SeriesName),
            Path = episode.Path,
            Duration = duration,
            IntroFingerprintEnd = fingerprintDuration,
            CreditsFingerprintStart = Math.Max(0, duration - maxCreditsDuration),
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
            (series.Tags.Contains("anime", StringComparison.OrdinalIgnoreCase) ||
             series.Genres.Contains("anime", StringComparison.OrdinalIgnoreCase)))
        {
            return QueuedMediaCategory.AnimeEpisode;
        }

        return QueuedMediaCategory.Episode;
    }

    private void QueueMovie(Movie movie)
    {
        var pluginInstance = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance was null");

        if (string.IsNullOrEmpty(movie.Path))
        {
            _logger.LogWarning(
                "Not queuing movie \"{Name}\" ({Id}) as no path was provided by Jellyfin",
                movie.Name,
                movie.Id);
            return;
        }

        // Allocate a new list for each Movie
        _queuedEpisodes.TryAdd(movie.Id, []);

        var duration = TimeSpan.FromTicks(movie.RunTimeTicks ?? 0).TotalSeconds;

        _queuedEpisodes[movie.Id].Add(new QueuedEpisode
        {
            SeriesName = movie.Name,
            SeriesId = movie.Id,
            SeasonId = movie.Id,
            EpisodeId = movie.Id,
            Name = movie.Name,
            Path = movie.Path,
            Duration = duration,
            CreditsFingerprintStart = Math.Max(0, duration - pluginInstance.Configuration.MaximumMovieCreditsDuration),
            Category = QueuedMediaCategory.Movie,
            IsExcluded = IsSeriesExcluded(movie.Name),
        });

        pluginInstance.TotalQueued++;
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
            _logger.LogInformation("Episode {Name} ({Id}) has an invalid SeasonId", episode.Name, episode.Id);
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
                _logger.LogWarning("Failed to resolve SeasonId for episode {Name} ({Id}) after metadata refresh", episode.Name, episode.Id);
            }
            else
            {
                _logger.LogDebug("Successfully resolved SeasonId {SeasonId} for episode {Name} ({Id})", episode.SeasonId, episode.Name, episode.Id);
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
    /// <returns>Media items that have been verified to exist in Jellyfin and in storage.</returns>
    internal IReadOnlyList<QueuedEpisode> VerifyQueue(IReadOnlyList<QueuedEpisode> candidates, IReadOnlyCollection<AnalysisMode> modes)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return [];
        }

        var verified = new List<QueuedEpisode>(candidates.Count);
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is null");
        var episodeIds = plugin.GetEpisodeIds(candidates[0].SeasonId);

        foreach (var candidate in candidates)
        {
            try
            {
                var path = plugin.GetItemPath(candidate.EpisodeId);

                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    _logger.LogDebug("Skipping {Name} ({Id}): file not found", candidate.Name, candidate.EpisodeId);
                    continue;
                }

                verified.Add(candidate);

                var hasSegments = plugin.GetTimestamps(candidate.EpisodeId);

                foreach (var mode in modes)
                {
                    if (hasSegments.TryGetValue(mode, out var seg))
                    {
                        candidate.SetAnalyzed(mode, EpisodeState.Analyzed);
                    }
                    else if (!plugin.AnalyzeAgain && episodeIds.TryGetValue(mode, out var ids) && ids.Contains(candidate.EpisodeId))
                    {
                        candidate.SetAnalyzed(mode, EpisodeState.NoSegments);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Skipping analysis of {Name} ({Id}): {Exception}",
                    candidate.Name,
                    candidate.EpisodeId,
                    ex);
            }
        }

        return verified;
    }

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex NormalizeSeriesNameRegex();
}
