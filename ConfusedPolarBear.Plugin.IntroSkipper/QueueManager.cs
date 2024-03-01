namespace ConfusedPolarBear.Plugin.IntroSkipper;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

/// <summary>
/// Manages enqueuing library items for analysis.
/// </summary>
public class QueueManager
{
    private ILibraryManager _libraryManager;
    private ILogger<QueueManager> _logger;

    private double analysisPercent;
    private List<string> selectedLibraries;
    private Dictionary<Guid, List<QueuedEpisode>> _queuedEpisodes;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueManager"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="libraryManager">Library manager.</param>
    public QueueManager(ILogger<QueueManager> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;

        selectedLibraries = new();
        _queuedEpisodes = new();
    }

    /// <summary>
    /// Gets all media items on the server.
    /// </summary>
    /// <returns>Queued media items.</returns>
    public ReadOnlyDictionary<Guid, List<QueuedEpisode>> GetMediaItems()
    {
        // Assert that ffmpeg with chromaprint is installed
        if (!FFmpegWrapper.CheckFFmpegVersion())
        {
            throw new FingerprintException(
                "ffmpeg with chromaprint is not installed on this system - episodes will not be analyzed. If Jellyfin is running natively, install jellyfin-ffmpeg5. If Jellyfin is running in a container, upgrade it to the latest version of 10.8.0.");
        }

        Plugin.Instance!.TotalQueued = 0;

        LoadAnalysisSettings();

        // For all selected libraries, enqueue all contained episodes.
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            // If libraries have been selected for analysis, ensure this library was selected.
            if (selectedLibraries.Count > 0 && !selectedLibraries.Contains(folder.Name))
            {
                _logger.LogDebug("Not analyzing library \"{Name}\": not selected by user", folder.Name);
                continue;
            }

            _logger.LogInformation(
                "Running enqueue of items in library {Name}",
                folder.Name);

            try
            {
                foreach (var location in folder.Locations)
                {
                    QueueLibraryContents(_libraryManager.FindByPath(location, true).Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to enqueue items from library {Name}: {Exception}", folder.Name, ex);
            }
        }

        Plugin.Instance!.TotalSeasons = _queuedEpisodes.Count;
        Plugin.Instance!.QueuedMediaItems.Clear();
        foreach (var kvp in _queuedEpisodes)
        {
            Plugin.Instance!.QueuedMediaItems[kvp.Key] = kvp.Value;
        }

        return new(_queuedEpisodes);
    }

    /// <summary>
    /// Loads the list of libraries which have been selected for analysis and the minimum intro duration.
    /// Settings which have been modified from the defaults are logged.
    /// </summary>
    private void LoadAnalysisSettings()
    {
        var config = Plugin.Instance!.Configuration;

        // Store the analysis percent
        analysisPercent = Convert.ToDouble(config.AnalysisPercent) / 100;

        // Get the list of library names which have been selected for analysis, ignoring whitespace and empty entries.
        selectedLibraries = config.SelectedLibraries
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // If any libraries have been selected for analysis, log their names.
        if (selectedLibraries.Count > 0)
        {
            _logger.LogInformation("Limiting analysis to the following libraries: {Selected}", selectedLibraries);
        }
        else
        {
            _logger.LogDebug("Not limiting analysis by library name");
        }

        // If analysis settings have been changed from the default, log the modified settings.
        if (config.AnalysisLengthLimit != 10 || config.AnalysisPercent != 25 || config.MinimumIntroDuration != 15)
        {
            _logger.LogInformation(
                "Analysis settings have been changed to: {Percent}%/{Minutes}m and a minimum of {Minimum}s",
                config.AnalysisPercent,
                config.AnalysisLengthLimit,
                config.MinimumIntroDuration);
        }
    }

    private void QueueLibraryContents(Guid id)
    {
        _logger.LogDebug("Constructing anonymous internal query");

        var query = new InternalItemsQuery()
        {
            // Order by series name, season, and then episode number so that status updates are logged in order
            ParentId = id,
            OrderBy = new[]
            {
                ("SeriesSortName", SortOrder.Ascending),
                ("ParentIndexNumber", SortOrder.Ascending),
                ("IndexNumber", SortOrder.Ascending),
            },
            IncludeItemTypes = new BaseItemKind[] { BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false
        };

        _logger.LogDebug("Getting items");

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
            if (item is not Episode episode)
            {
                _logger.LogDebug("Item {Name} is not an episode", item.Name);
                continue;
            }

            QueueEpisode(episode);
        }

        _logger.LogDebug("Queued {Count} episodes", items.Count);
    }

    private void QueueEpisode(Episode episode)
    {
        if (Plugin.Instance is null)
        {
            throw new InvalidOperationException("plugin instance was null");
        }

        if (string.IsNullOrEmpty(episode.Path))
        {
            _logger.LogWarning(
                "Not queuing episode \"{Name}\" from series \"{Series}\" ({Id}) as no path was provided by Jellyfin",
                episode.Name,
                episode.SeriesName,
                episode.Id);
            return;
        }

        // Limit analysis to the first X% of the episode and at most Y minutes.
        // X and Y default to 25% and 10 minutes.
        var duration = TimeSpan.FromTicks(episode.RunTimeTicks ?? 0).TotalSeconds;
        var fingerprintDuration = duration;

        if (fingerprintDuration >= 5 * 60)
        {
            fingerprintDuration *= analysisPercent;
        }

        fingerprintDuration = Math.Min(
            fingerprintDuration,
            60 * Plugin.Instance!.Configuration.AnalysisLengthLimit);

        // Allocate a new list for each new season
        _queuedEpisodes.TryAdd(episode.SeasonId, new List<QueuedEpisode>());

        // Queue the episode for analysis
        var maxCreditsDuration = Plugin.Instance!.Configuration.MaximumEpisodeCreditsDuration;
        _queuedEpisodes[episode.SeasonId].Add(new QueuedEpisode()
        {
            SeriesName = episode.SeriesName,
            SeasonNumber = episode.AiredSeasonNumber ?? 0,
            EpisodeId = episode.Id,
            Name = episode.Name,
            Path = episode.Path,
            Duration = Convert.ToInt32(duration),
            IntroFingerprintEnd = Convert.ToInt32(fingerprintDuration),
            CreditsFingerprintStart = Convert.ToInt32(duration - maxCreditsDuration),
        });

        Plugin.Instance!.TotalQueued++;
    }

    /// <summary>
    /// Verify that a collection of queued media items still exist in Jellyfin and in storage.
    /// This is done to ensure that we don't analyze items that were deleted between the call to GetMediaItems() and popping them from the queue.
    /// </summary>
    /// <param name="candidates">Queued media items.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>Media items that have been verified to exist in Jellyfin and in storage.</returns>
    public (ReadOnlyCollection<QueuedEpisode> VerifiedItems, bool AnyUnanalyzed)
        VerifyQueue(ReadOnlyCollection<QueuedEpisode> candidates, AnalysisMode mode)
    {
        var unanalyzed = false;
        var verified = new List<QueuedEpisode>();

        var timestamps = mode == AnalysisMode.Introduction ?
                Plugin.Instance!.Intros :
                Plugin.Instance!.Credits;

        foreach (var candidate in candidates)
        {
            try
            {
                var path = Plugin.Instance!.GetItemPath(candidate.EpisodeId);

                if (File.Exists(path))
                {
                    verified.Add(candidate);
                }

                if (!timestamps.ContainsKey(candidate.EpisodeId))
                {
                    unanalyzed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Skipping {Mode} analysis of {Name} ({Id}): {Exception}",
                    mode,
                    candidate.Name,
                    candidate.EpisodeId,
                    ex);
            }
        }

        return (verified.AsReadOnly(), unanalyzed);
    }
}
