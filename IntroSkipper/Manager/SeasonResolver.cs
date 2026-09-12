// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Resolves Jellyfin library items into the seasons the analyzers work on, one series at
/// a time. A series' non-virtual episodes are fetched through the series, so a flat series
/// folder resolves like season folders. An in-season special (stored in Season 0, airing
/// within another season) belongs to its host season when a non-virtual season with that
/// number exists, otherwise to Specials. A movie is a season of one keyed by its own id.
/// Stateless: one instance serves every pass, the watcher and the dashboard.
/// </summary>
/// <param name="logger">Logger.</param>
/// <param name="libraryManager">Library manager.</param>
public sealed partial class SeasonResolver(ILogger<SeasonResolver> logger, ILibraryManager libraryManager)
{
    private readonly ILogger<SeasonResolver> _logger = logger;
    private readonly ILibraryManager _libraryManager = libraryManager;

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Returns the season key of an item: a movie's own id, an episode's season id, or the
    /// host season's id for an in-season special. An episode Jellyfin attached to no
    /// season is keyed by its own id, so it is still analyzed and can still be disabled.
    /// Looks up the series' seasons only for Season 0 episodes and episodes without a season id.
    /// </summary>
    /// <param name="item">An episode or movie.</param>
    /// <returns>The season key.</returns>
    internal Guid SeasonKey(BaseItem item)
        => item is Episode episode
            ? SeasonKey(episode, new Lazy<IReadOnlyDictionary<int, Guid>>(() => SeasonsByNumber(episode.SeriesId)))
            : item.Id;

    /// <summary>
    /// Returns whether the id names a season or movie the server knows.
    /// </summary>
    /// <param name="key">A season key.</param>
    /// <returns><see langword="true"/> if the key resolves to a season or movie; otherwise, <see langword="false"/>.</returns>
    internal bool IsKnownKey(Guid key) => FindKeyItem(key) is not null;

    /// <summary>
    /// Resolves one season key. A known season with nothing to analyze resolves to a
    /// season without episodes, not to <see langword="null"/>.
    /// </summary>
    /// <param name="key">A season key.</param>
    /// <param name="includeExcluded"><see langword="true"/> to keep episodes the exclusion policy matches, flagged; otherwise, <see langword="false"/>.</param>
    /// <returns>The resolved season, or <see langword="null"/> when the key is not a season or movie the server knows.</returns>
    internal ResolvedSeason? ResolveKey(Guid key, bool includeExcluded = false)
    {
        switch (FindKeyItem(key))
        {
            case Movie movie:
                return ResolveMovie(movie, ExclusionPolicy.FromConfiguration(Config), includeExcluded) ?? new ResolvedSeason(key, key, []);
            case Season season:
                if (FindSeries(season.SeriesId) is not { } series)
                {
                    return null;
                }

                return Resolve(series, includeExcluded).FirstOrDefault(resolved => resolved.Key == key) ?? new ResolvedSeason(key, series.Id, []);
            default:
                return null;
        }
    }

    /// <summary>
    /// Lists the series and movies of every library the plugin is enabled for, in sort
    /// name order. A library that cannot be enumerated is logged and skipped.
    /// </summary>
    /// <returns>The series and movies to resolve.</returns>
    internal IReadOnlyList<BaseItem> EnumerateLibrary() => EnumerateLibrary(out _);

    /// <summary>
    /// Returns the series or movie each key belongs to, once each, so a scoped pass
    /// resolves only the series it needs. Unknown keys are dropped.
    /// </summary>
    /// <param name="keys">Season keys.</param>
    /// <returns>The series and movies to resolve.</returns>
    internal IReadOnlyList<BaseItem> OwnersOf(IEnumerable<Guid> keys)
    {
        List<BaseItem> owners = [];
        foreach (var key in keys)
        {
            BaseItem? owner = FindKeyItem(key) switch
            {
                Movie movie => movie,
                Season season => FindSeries(season.SeriesId),
                _ => null,
            };

            if (owner is not null)
            {
                owners.Add(owner);
            }
        }

        return owners.DistinctBy(owner => owner.Id).ToList();
    }

    /// <summary>
    /// Resolves a series into its seasons, or a movie into its season of one. Episodes and
    /// movies without a path are logged and skipped.
    /// </summary>
    /// <param name="seriesOrMovie">A series or movie from <see cref="EnumerateLibrary()"/> or <see cref="OwnersOf"/>.</param>
    /// <param name="includeExcluded"><see langword="true"/> to keep episodes the exclusion policy matches, flagged; otherwise, <see langword="false"/>.</param>
    /// <returns>The resolved seasons; empty when nothing remains to analyze.</returns>
    /// <exception cref="InvalidOperationException">The library returned no result for the series' episodes.</exception>
    internal IReadOnlyList<ResolvedSeason> Resolve(BaseItem seriesOrMovie, bool includeExcluded = false)
    {
        var policy = ExclusionPolicy.FromConfiguration(Config);
        return seriesOrMovie switch
        {
            Movie movie => ResolveMovie(movie, policy, includeExcluded) is { } season ? [season] : [],
            Series series => ResolveSeries(series, policy, includeExcluded),
            _ => [],
        };
    }

    /// <summary>
    /// Resolves every season of every enabled library, sequentially, counting the
    /// libraries and series that failed so maintenance can refuse to act on an
    /// incomplete result.
    /// </summary>
    /// <param name="includeExcluded"><see langword="true"/> to keep episodes the exclusion policy matches, flagged; otherwise, <see langword="false"/>.</param>
    /// <param name="cancellationToken">Cancellation token, checked between series.</param>
    /// <returns>The seasons and the failure count.</returns>
    internal LibraryResolution ResolveLibrary(bool includeExcluded, CancellationToken cancellationToken)
    {
        var owners = EnumerateLibrary(out var failures);
        List<ResolvedSeason> seasons = [];
        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                seasons.AddRange(Resolve(owner, includeExcluded));
            }
            catch (Exception ex)
            {
                failures++;
                LogFailedResolve(_logger, ex, owner.Name, owner.Id);
            }
        }

        return new LibraryResolution(seasons, failures);
    }

    internal static DateTime EpisodeAvailabilityDate(Episode episode)
        => episode.DateCreated != default ? episode.DateCreated : episode.DateLastSaved;

    private static bool IsInSeasonSpecial(Episode episode)
        => episode.ParentIndexNumber == 0 && episode.AiredSeasonNumber != 0;

    private static Guid SeasonKey(Episode episode, Lazy<IReadOnlyDictionary<int, Guid>> seasonsByNumber)
    {
        if (IsInSeasonSpecial(episode) && episode.AiredSeasonNumber is { } airedSeasonNumber
            && seasonsByNumber.Value.TryGetValue(airedSeasonNumber, out var hostSeasonId))
        {
            return hostSeasonId;
        }

        if (episode.SeasonId != Guid.Empty)
        {
            return episode.SeasonId;
        }

        return episode.ParentIndexNumber is { } seasonNumber && seasonsByNumber.Value.TryGetValue(seasonNumber, out var seasonId)
            ? seasonId
            : episode.Id;
    }

    private static long? FileVersion(BaseItem item)
        => item.DateModified == DateTime.MinValue ? null : item.DateModified.Ticks;

    private static InternalItemsQuery Query(params BaseItemKind[] kinds) => new()
    {
        IncludeItemTypes = kinds,
        Recursive = true,
        IsVirtualItem = false,
    };

    private IReadOnlyList<BaseItem> EnumerateLibrary(out int failures)
    {
        failures = 0;
        var policy = ExclusionPolicy.FromConfiguration(Config);
        if (policy.BroadPathRootCount > 0)
        {
            LogBroadPathRootExclusions(_logger, policy.BroadPathRootCount);
        }

        var virtualFolders = _libraryManager.GetVirtualFolders();
        if (virtualFolders is null)
        {
            failures++;
            LogLibraryManagerNull(_logger);
            return [];
        }

        List<BaseItem> owners = [];
        foreach (var folder in virtualFolders)
        {
            if (folder.LibraryOptions?.DisabledMediaSegmentProviders?.Contains(Plugin.Instance!.Name) == true)
            {
                LogLibraryDisabled(_logger, folder.Name);
                continue;
            }

            // Some virtual folders don't have a proper item id.
            if (!Guid.TryParse(folder.ItemId, out var folderId))
            {
                failures++;
                LogInvalidFolderId(_logger, folder.Name);
                continue;
            }

            LogEnumeratingLibrary(_logger, folder.Name);
            try
            {
                var query = Query(BaseItemKind.Series, BaseItemKind.Movie);
                query.ParentId = folderId;
                query.OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)];
                var items = _libraryManager.GetItemList(query, false);
                if (items is null)
                {
                    failures++;
                    LogLibraryQueryNull(_logger);
                    continue;
                }

                owners.AddRange(items);
            }
            catch (Exception ex)
            {
                failures++;
                LogFailedEnumerateLibrary(_logger, folder.Name, ex);
            }
        }

        // GetItemList has returned the same item twice.
        return owners.DistinctBy(owner => owner.Id).ToList();
    }

    private IReadOnlyList<ResolvedSeason> ResolveSeries(Series series, ExclusionPolicy policy, bool includeExcluded)
    {
        var query = Query(BaseItemKind.Episode);
        query.AncestorIds = [series.Id];
        query.OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Descending), (ItemSortBy.IndexNumber, SortOrder.Ascending)];
        var items = _libraryManager.GetItemList(query, false)
            ?? throw new InvalidOperationException($"Library query for the episodes of {series.Name} ({series.Id}) returned null");

        var category = SeriesHelper.IsAnime(series) ? QueuedMediaCategory.AnimeEpisode : QueuedMediaCategory.Episode;
        var seasonsByNumber = new Lazy<IReadOnlyDictionary<int, Guid>>(() => SeasonsByNumber(series.Id));
        var config = Config;
        var analysisPercent = config.AnalysisPercent / 100.0;

        // Seasons keep the order their first episode appeared in.
        List<Guid> keys = [];
        Dictionary<Guid, List<QueuedEpisode>> episodesByKey = [];
        foreach (var item in items.DistinctBy(item => item.Id))
        {
            if (item is not Episode episode)
            {
                continue;
            }

            if (string.IsNullOrEmpty(episode.Path))
            {
                LogNotQueuingEpisodeNoPath(_logger, episode.Name, series.Name, episode.Id);
                continue;
            }

            var decision = policy.EvaluateSeries(series.Name, episode.Path);
            if (decision.IsExcluded && !includeExcluded)
            {
                LogSkippingExcludedItem(_logger, episode.Name, decision.RuleLabel);
                continue;
            }

            var key = SeasonKey(episode, seasonsByNumber);
            if (!episodesByKey.TryGetValue(key, out var episodes))
            {
                episodes = [];
                episodesByKey[key] = episodes;
                keys.Add(key);
            }

            var duration = TimeSpan.FromTicks(episode.RunTimeTicks ?? 0).TotalSeconds;
            episodes.Add(new QueuedEpisode
            {
                SeriesName = series.Name,
                SeasonNumber = episode.AiredSeasonNumber ?? 0,
                SeriesId = series.Id,
                SeasonId = key,
                EpisodeNumber = episode.IndexNumber ?? 0,
                EpisodeId = episode.Id,
                Name = episode.Name,
                Category = category,
                IsExcluded = decision.IsExcluded,
                Path = episode.Path,
                Duration = duration,
                DateAdded = EpisodeAvailabilityDate(episode),
                IntroFingerprintEnd = Math.Min(duration >= 5 * 60 ? duration * analysisPercent : duration, 60 * config.AnalysisLengthLimit),
                FileVersion = FileVersion(episode),
            });
        }

        return keys.Select(key => new ResolvedSeason(key, series.Id, episodesByKey[key])).ToList();
    }

    private ResolvedSeason? ResolveMovie(Movie movie, ExclusionPolicy policy, bool includeExcluded)
    {
        if (string.IsNullOrEmpty(movie.Path))
        {
            LogNotQueuingMovieNoPath(_logger, movie.Name, movie.Id);
            return null;
        }

        var decision = policy.EvaluateMovie(movie.Name, movie.Path);
        if (decision.IsExcluded && !includeExcluded)
        {
            LogSkippingExcludedItem(_logger, movie.Name, decision.RuleLabel);
            return null;
        }

        var queued = new QueuedEpisode
        {
            SeriesName = movie.Name,
            SeriesId = movie.Id,
            SeasonId = movie.Id,
            EpisodeId = movie.Id,
            Name = movie.Name,
            Category = QueuedMediaCategory.Movie,
            IsExcluded = decision.IsExcluded,
            Path = movie.Path,
            Duration = TimeSpan.FromTicks(movie.RunTimeTicks ?? 0).TotalSeconds,
            FileVersion = FileVersion(movie),
        };
        return new ResolvedSeason(movie.Id, movie.Id, [queued]);
    }

    // The series' non-virtual seasons by number. A virtual season has no present
    // episodes, so it can host nothing and the library pass never reaches it.
    private IReadOnlyDictionary<int, Guid> SeasonsByNumber(Guid seriesId)
    {
        if (seriesId == Guid.Empty)
        {
            return new Dictionary<int, Guid>();
        }

        var query = Query(BaseItemKind.Season);
        query.AncestorIds = [seriesId];
        Dictionary<int, Guid> seasons = [];
        foreach (var season in (_libraryManager.GetItemList(query, false) ?? []).OfType<Season>())
        {
            if (season.IndexNumber is { } number)
            {
                seasons.TryAdd(number, season.Id);
            }
        }

        return seasons;
    }

    private BaseItem? FindKeyItem(Guid key)
    {
        var item = key != Guid.Empty ? _libraryManager.GetItemById(key) : null;
        return item is Movie or Season ? item : null;
    }

    private Series? FindSeries(Guid seriesId)
        => seriesId != Guid.Empty ? _libraryManager.GetItemById(seriesId) as Series : null;

    [LoggerMessage(Level = LogLevel.Error, Message = "Library manager returned null when requesting virtual folders")]
    private static partial void LogLibraryManagerNull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping library \"{Name}\": virtual folder does not have a valid item id")]
    private static partial void LogInvalidFolderId(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Not analyzing library \"{Name}\": Intro Skipper is disabled in library settings. To enable, check library configuration > Media Segment Providers")]
    private static partial void LogLibraryDisabled(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enumerating library {Name}")]
    private static partial void LogEnumeratingLibrary(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to enumerate library {Name}")]
    private static partial void LogFailedEnumerateLibrary(ILogger logger, string name, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Library query result is null")]
    private static partial void LogLibraryQueryNull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to resolve {Name} ({Id})")]
    private static partial void LogFailedResolve(ILogger logger, Exception exception, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping excluded item {Name}: matched {RuleLabel}")]
    private static partial void LogSkippingExcludedItem(ILogger logger, string name, string ruleLabel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configured path exclusions include {Count} filesystem root or drive root entries")]
    private static partial void LogBroadPathRootExclusions(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not queuing episode \"{Name}\" from series \"{Series}\" ({Id}) as no path was provided by Jellyfin")]
    private static partial void LogNotQueuingEpisodeNoPath(ILogger logger, string name, string series, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not queuing movie \"{Name}\" ({Id}) as no path was provided by Jellyfin")]
    private static partial void LogNotQueuingMovieNoPath(ILogger logger, string name, Guid id);
}
