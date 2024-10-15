using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Manager
{
    /// <summary>
    /// Manages enqueuing library items for analysis.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="QueueManager"/> class.
    /// </remarks>
    /// <param name="logger">Logger.</param>
    /// <param name="libraryManager">Library manager.</param>
    public class QueueManager(ILogger<QueueManager> logger, ILibraryManager libraryManager)
    {
        private readonly ILibraryManager _libraryManager = libraryManager;
        private readonly ILogger<QueueManager> _logger = logger;
        private readonly Dictionary<Guid, List<QueuedEpisode>> _queuedEpisodes = [];
        private double _analysisPercent;
        private List<string> _selectedLibraries = [];
        private bool _selectAllLibraries;

        /// <summary>
        /// Gets all media items on the server.
        /// </summary>
        /// <returns>Queued media items.</returns>
        public IReadOnlyDictionary<Guid, List<QueuedEpisode>> GetMediaItems()
        {
            Plugin.Instance!.TotalQueued = 0;

            LoadAnalysisSettings();

            // For all selected libraries, enqueue all contained episodes.
            foreach (var folder in _libraryManager.GetVirtualFolders())
            {
                // If libraries have been selected for analysis, ensure this library was selected.
                if (!_selectAllLibraries && !_selectedLibraries.Contains(folder.Name))
                {
                    _logger.LogDebug("Not analyzing library \"{Name}\": not selected by user", folder.Name);
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
                    QueueLibraryContents(folderId);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Failed to enqueue items from library {Name}: {Exception}", folder.Name, ex);
                }
            }

            Plugin.Instance.TotalSeasons = _queuedEpisodes.Count;
            Plugin.Instance.QueuedMediaItems.Clear();
            foreach (var kvp in _queuedEpisodes)
            {
                Plugin.Instance.QueuedMediaItems.TryAdd(kvp.Key, kvp.Value);
            }

            return _queuedEpisodes;
        }

        /// <summary>
        /// Loads the list of libraries which have been selected for analysis and the minimum intro duration.
        /// Settings which have been modified from the defaults are logged.
        /// </summary>
        private void LoadAnalysisSettings()
        {
            var config = Plugin.Instance!.Configuration;

            // Store the analysis percent
            _analysisPercent = Convert.ToDouble(config.AnalysisPercent) / 100;

            _selectAllLibraries = config.SelectAllLibraries;

            if (!_selectAllLibraries)
            {
                // Get the list of library names which have been selected for analysis, ignoring whitespace and empty entries.
                _selectedLibraries = [.. config.SelectedLibraries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

                // If any libraries have been selected for analysis, log their names.
                _logger.LogInformation("Limiting analysis to the following libraries: {Selected}", _selectedLibraries);
            }
            else
            {
                _logger.LogDebug("Not limiting analysis by library name");
            }

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

        private void QueueLibraryContents(Guid id)
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

            var items = _libraryManager.GetItemList(query, false);

            if (items is null)
            {
                _logger.LogError("Library query result is null");
                return;
            }

            // Queue all episodes on the server for fingerprinting.
            _logger.LogDebug("Iterating through library items");

            foreach (var item in items)
            {
                if (item is Episode episode)
                {
                    QueueEpisode(episode);
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

            _logger.LogDebug("Queued {Count} episodes", items.Count);
        }

        private void QueueEpisode(Episode episode)
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
            var seasonId = GetSeasonId(episode);
            if (!_queuedEpisodes.TryGetValue(seasonId, out var seasonEpisodes))
            {
                seasonEpisodes = [];
                _queuedEpisodes[seasonId] = seasonEpisodes;
            }

            if (seasonEpisodes.Any(e => e.EpisodeId == episode.Id))
            {
                _logger.LogDebug(
                    "\"{Name}\" from series \"{Series}\" ({Id}) is already queued",
                    episode.Name,
                    episode.SeriesName,
                    episode.Id);
                return;
            }

            var isAnime = seasonEpisodes.FirstOrDefault()?.IsAnime ??
                (pluginInstance.GetItem(episode.SeriesId) is Series series &&
                    (series.Tags.Contains("anime", StringComparison.OrdinalIgnoreCase) ||
                    series.Genres.Contains("anime", StringComparison.OrdinalIgnoreCase)));

            // Limit analysis to the first X% of the episode and at most Y minutes.
            // X and Y default to 25% and 10 minutes.
            var duration = TimeSpan.FromTicks(episode.RunTimeTicks ?? 0).TotalSeconds;
            var fingerprintDuration = Math.Min(
                duration >= 5 * 60 ? duration * _analysisPercent : duration,
                60 * pluginInstance.Configuration.AnalysisLengthLimit);

            // Queue the episode for analysis
            var maxCreditsDuration = pluginInstance.Configuration.MaximumCreditsDuration;
            seasonEpisodes.Add(new QueuedEpisode
            {
                SeriesName = episode.SeriesName,
                SeasonNumber = episode.AiredSeasonNumber ?? 0,
                SeriesId = episode.SeriesId,
                EpisodeId = episode.Id,
                Name = episode.Name,
                IsAnime = isAnime,
                Path = episode.Path,
                Duration = Convert.ToInt32(duration),
                IntroFingerprintEnd = Convert.ToInt32(fingerprintDuration),
                CreditsFingerprintStart = Convert.ToInt32(duration - maxCreditsDuration),
            });

            pluginInstance.TotalQueued++;
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

            // Limit analysis to the first X% of the episode and at most Y minutes.
            // X and Y default to 25% and 10 minutes.
            var duration = TimeSpan.FromTicks(movie.RunTimeTicks ?? 0).TotalSeconds;
            var fingerprintDuration = Math.Min(
                duration >= 5 * 60 ? duration * _analysisPercent : duration,
                60 * pluginInstance.Configuration.AnalysisLengthLimit);

            // Queue the episode for analysis
            var maxCreditsDuration = pluginInstance.Configuration.MaximumCreditsDuration;
            _queuedEpisodes[movie.Id].Add(new QueuedEpisode
            {
                SeriesName = movie.Name,
                SeriesId = movie.Id,
                EpisodeId = movie.Id,
                Name = movie.Name,
                Path = movie.Path,
                Duration = Convert.ToInt32(duration),
                IntroFingerprintEnd = Convert.ToInt32(fingerprintDuration),
                CreditsFingerprintStart = Convert.ToInt32(duration - maxCreditsDuration),
                IsMovie = true
            });

            pluginInstance.TotalQueued++;
        }

        private Guid GetSeasonId(Episode episode)
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

            return episode.SeasonId;
        }

        /// <summary>
        /// Verify that a collection of queued media items still exist in Jellyfin and in storage.
        /// This is done to ensure that we don't analyze items that were deleted between the call to GetMediaItems() and popping them from the queue.
        /// </summary>
        /// <param name="candidates">Queued media items.</param>
        /// <param name="modes">Analysis mode.</param>
        /// <returns>Media items that have been verified to exist in Jellyfin and in storage.</returns>
        public (IReadOnlyList<QueuedEpisode> VerifiedItems, IReadOnlyCollection<AnalysisMode> RequiredModes)
            VerifyQueue(IReadOnlyList<QueuedEpisode> candidates, IReadOnlyCollection<AnalysisMode> modes)
        {
            var verified = new List<QueuedEpisode>();
            var reqModes = new HashSet<AnalysisMode>();

            foreach (var candidate in candidates)
            {
                try
                {
                    var path = Plugin.Instance!.GetItemPath(candidate.EpisodeId);

                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    verified.Add(candidate);

                    foreach (var mode in modes)
                    {
                        if (candidate.State.IsAnalyzed(mode) || candidate.State.IsBlacklisted(mode))
                        {
                            continue;
                        }

                        bool isAnalyzed = mode == AnalysisMode.Introduction
                            ? Plugin.Instance!.Intros.ContainsKey(candidate.EpisodeId)
                            : Plugin.Instance!.Credits.ContainsKey(candidate.EpisodeId);

                        if (isAnalyzed)
                        {
                            candidate.State.SetAnalyzed(mode, true);
                        }
                        else
                        {
                            reqModes.Add(mode);
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

            return (verified, reqModes);
        }
    }
}
