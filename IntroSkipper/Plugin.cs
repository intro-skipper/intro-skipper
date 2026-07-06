// SPDX-FileCopyrightText: 2019 dkanada
// SPDX-FileCopyrightText: 2019 Phallacy
// SPDX-FileCopyrightText: 2021 Cody Robibero
// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Services;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntroSkipper;

/// <summary>
/// Intro skipper plugin. Uses audio analysis to find common sequences of audio shared between episodes.
/// </summary>
/// <remarks>
/// The database methods on this type are transitional delegates to the store layer
/// (<see cref="ISegmentStore"/>, <see cref="ISeasonStateStore"/>, <see cref="ISegmentUpdateService"/>)
/// kept only for call sites that are still constructed manually (analyzers, QueueManager,
/// BaseItemAnalyzerTask). New code must inject the stores via DI instead; see
/// docs/db-redesign/theory-a.md for the target end state with zero database code in this class.
/// </remarks>
public partial class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILibraryManager _libraryManager;
    private readonly IChapterManager _chapterRepository;
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<Plugin> _logger;
    private readonly string _dbPath;
    private readonly string _cacheDbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="serverConfiguration">Server configuration manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="chapterRepository">Chapter repository.</param>
    /// <param name="pluginManager">Plugin manager.</param>
    /// <param name="logger">Logger.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServerConfigurationManager serverConfiguration,
        ILibraryManager libraryManager,
        IChapterManager chapterRepository,
        IPluginManager pluginManager,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        _libraryManager = libraryManager;
        _chapterRepository = chapterRepository;
        _pluginManager = pluginManager;
        _logger = logger;

        FFmpegPath = serverConfiguration.GetEncodingOptions().EncoderAppPathDisplay;

        ArgumentNullException.ThrowIfNull(applicationPaths);

        var pluginCachePath = "chromaprints";

        var introsDirectory = PluginDatabasePaths.GetPluginDataDirectory(applicationPaths);
        FingerprintCachePath = Path.Join(introsDirectory, pluginCachePath);

        _dbPath = PluginDatabasePaths.GetSegmentDbPath(applicationPaths);
        _cacheDbPath = PluginDatabasePaths.GetCacheDbPath(applicationPaths);

        // Create the base directories (if needed).
        // Directory.CreateDirectory is already a no-op when the directory exists, so we can call it unconditionally without checking first.
        Directory.CreateDirectory(introsDirectory);

        // Initialize segment database.
        try
        {
            using var db = CreateDbContext();
            // Legacy databases may be missing migration history or columns that EF migrations expect.
            // Normalize those schemas first so recovery does not log a false initialization failure.
            db.EnsureLegacySchemaCompatibility();
            db.ApplyMigrations();
        }
        catch (Exception ex)
        {
            LogDatabaseInitializationError(_logger, ex);
        }

        // Initialize detection cache database.
        try
        {
            using var cacheDb = CreateCacheDbContext();
            cacheDb.EnsureSchema();
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            LogCacheDbInitializationError(_logger, ex);
        }

        MigrateLegacyExcludeSeries();

        Configuration.FileTransformationPluginEnabled = _pluginManager
            .Plugins
            .Any(p => p.Id == Guid.Parse("5e87cc92-571a-4d8d-8d98-d2d4147f9f90")); // File Transformation plugin ID
    }

    /// <summary>
    /// Gets the path to the segment database.
    /// </summary>
    public string DbPath => _dbPath;

    /// <summary>
    /// Gets the path to the detection cache database.
    /// </summary>
    public string CacheDbPath => _cacheDbPath;

    /// <summary>
    /// Gets or sets a value indicating whether to analyze again.
    /// </summary>
    public bool AnalyzeAgain { get; set; }

    /// <summary>
    /// Gets the most recent media item queue.
    /// </summary>
    public ConcurrentDictionary<Guid, List<QueuedEpisode>> QueuedMediaItems { get; } = new();

    /// <summary>
    /// Gets or sets the total number of episodes in the queue.
    /// </summary>
    public int TotalQueued { get; set; }

    /// <summary>
    /// Gets or sets the number of seasons in the queue.
    /// </summary>
    public int TotalSeasons { get; set; }

    /// <summary>
    /// Gets the directory to cache fingerprints in.
    /// </summary>
    public string FingerprintCachePath { get; private set; }

    /// <summary>
    /// Gets the full path to FFmpeg.
    /// </summary>
    public string FFmpegPath { get; private set; }

    /// <inheritdoc />
    public override string Name => "Intro Skipper";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c83d86bb-a1e0-4c35-a113-e2101cf4ee6b");

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Creates a new <see cref="IntroSkipperDbContext"/> instance configured for the plugin database.
    /// </summary>
    /// <returns>A new <see cref="IntroSkipperDbContext"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the plugin has not been initialized.</exception>
    public static IntroSkipperDbContext CreateDbContext()
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return new IntroSkipperDbContext(Instance.DbPath);
    }

    /// <summary>
    /// Creates a new <see cref="DetectionCacheDbContext"/> instance configured for the plugin cache database.
    /// </summary>
    /// <returns>A new <see cref="DetectionCacheDbContext"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the plugin has not been initialized.</exception>
    public static DetectionCacheDbContext CreateCacheDbContext()
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return new DetectionCacheDbContext(Instance.CacheDbPath);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EnableInMainMenu = Instance?.Configuration.EnableMainMenu ?? true,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "introskipper.js",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.introskipper.js"
            },
            new PluginPageInfo
            {
                Name = "introskipper.css",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.introskipper.css"
            }
        ];
    }

    internal BaseItem? GetItem(Guid id) => id != Guid.Empty ? _libraryManager.GetItemById(id) : null;

    internal ICollection<Folder> GetCollectionFolders(Guid id) => GetItem(id) is var item && item is not null ? _libraryManager.GetCollectionFolders(item) : [];

    internal string GetItemPath(Guid id) => GetItem(id) is var item && item is not null ? item.Path : string.Empty;

    internal IReadOnlyList<ChapterInfo> GetChapters(Guid id) => _chapterRepository.GetChapters(id) ?? Array.Empty<ChapterInfo>();

    internal Task UpdateTimestampAsync(
        Segment segment,
        AnalysisMode mode,
        bool isUserProvided = false,
        string configHash = "",
        CancellationToken cancellationToken = default)
    {
        return CreateSegmentUpdateService().UpdateTimestampAsync(segment, mode, isUserProvided, configHash, cancellationToken);
    }

    internal static Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSegmentStore().GetTimestampsAsync(id, cancellationToken);
    }

    internal static Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSegmentStore().GetSegmentsAsync(id, cancellationToken);
    }

    internal static Task DeleteItemSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSegmentStore().DeleteSegmentsAsync(itemId, cancellationToken);
    }

    internal static Task CleanTimestampsAsync(IEnumerable<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSegmentStore().CleanTimestampsAsync(episodeIds as IReadOnlyCollection<Guid> ?? [.. episodeIds], cancellationToken);
    }

    internal static Task SetAnalyzerActionAsync(Guid id, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSeasonStateStore().SetAnalyzerActionsAsync(id, analyzerActions, cancellationToken);
    }

    internal static Task SetEpisodeIdsAsync(Guid id, AnalysisMode mode, IEnumerable<Guid> episodeIds, string configHash = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSeasonStateStore().SetEpisodeIdsAsync(id, mode, episodeIds, configHash, cancellationToken);
    }

    /// <summary>
    /// Removes a single episode ID from the season's analyzed-state list for the given mode.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="episodeId">Episode ID to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static Task RemoveEpisodeIdAsync(Guid seasonId, AnalysisMode mode, Guid episodeId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSeasonStateStore().RemoveEpisodeIdAsync(seasonId, mode, episodeId, cancellationToken);
    }

    /// <summary>
    /// Removes stale automatic segments for the supplied items and mode.
    /// User-provided segments are intentionally preserved.
    /// </summary>
    /// <param name="itemIds">Item IDs to inspect.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="configHash">Current configuration hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static Task CleanStaleAutomaticSegmentsAsync(
        IEnumerable<Guid> itemIds,
        AnalysisMode mode,
        string configHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSegmentStore().CleanStaleAutomaticSegmentsAsync(itemIds as IReadOnlyCollection<Guid> ?? [.. itemIds], mode, configHash, cancellationToken);
    }

    internal static Task<IReadOnlyDictionary<AnalysisMode, IEnumerable<Guid>>> GetEpisodeIdsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSeasonStateStore().GetEpisodeIdsAsync(id, cancellationToken);
    }

    /// <summary>
    /// Returns the settled-season reanalysis state for all modes in a season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Settled reanalysis state keyed by analysis mode.</returns>
    internal static Task<IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>> GetSettleReanalysisStatesAsync(
        Guid seasonId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSeasonStateStore().GetSettleReanalysisStatesAsync(seasonId, cancellationToken);
    }

    /// <summary>
    /// Returns whether a settled-season analysis mode still needs re-analysis for its current episode
    /// set. Pure set comparison: the decision is committed separately via
    /// <see cref="RecordSettleReanalysisAsync(Guid, IReadOnlyCollection{AnalysisMode}, IReadOnlyCollection{Guid}, CancellationToken)"/>
    /// once the reset has succeeded, so the completed episode set survives plugin restarts.
    /// </summary>
    /// <param name="settledEpisodeIds">Episode IDs recorded when the season was last settle-reanalyzed for this mode.</param>
    /// <param name="episodeIds">Current episode IDs in the season.</param>
    /// <returns><see langword="true"/> when a re-analysis should be performed; otherwise <see langword="false"/>.</returns>
    internal static bool ShouldSettleReanalyze(
        IReadOnlySet<Guid> settledEpisodeIds,
        IReadOnlyCollection<Guid> episodeIds)
        => settledEpisodeIds.Count != episodeIds.Count || episodeIds.Any(id => !settledEpisodeIds.Contains(id));

    /// <summary>
    /// Records that season analysis modes have been re-analyzed for the given episode set so the
    /// exact completed set is not repeated on subsequent scans or after a plugin restart. Call only
    /// after the reset has committed.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="modes">Analysis modes that were re-analyzed.</param>
    /// <param name="episodeIds">Episode IDs that were re-analyzed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static Task RecordSettleReanalysisAsync(
        Guid seasonId,
        IReadOnlyCollection<AnalysisMode> modes,
        IReadOnlyCollection<Guid> episodeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSeasonStateStore().RecordSettleReanalysisAsync(seasonId, modes, episodeIds, cancellationToken);
    }

    /// <summary>
    /// Clears stored automatic analysis state for a season so it is re-analyzed from scratch on the
    /// current pass. Automatic segments for the supplied modes are deleted and the season's analyzed
    /// episode lists are cleared; user-provided segments and the fingerprint cache are preserved.
    /// </summary>
    /// <param name="seasonId">Season ID whose analyzed-state lists should be cleared.</param>
    /// <param name="episodeIds">Episode IDs whose automatic segments should be removed.</param>
    /// <param name="modes">Analysis modes to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static Task ResetSeasonForReanalysisAsync(
        Guid seasonId,
        IEnumerable<Guid> episodeIds,
        IReadOnlyCollection<AnalysisMode> modes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSeasonStateStore().ResetSeasonForReanalysisAsync(seasonId, episodeIds, modes, cancellationToken);
    }

    internal static Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSeasonStateStore().GetSeasonQueueSnapshotAsync(seasonId, episodeIds, cancellationToken);
    }

    internal static Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSeasonStateStore().GetAllAnalyzerActionsAsync(seasonId, cancellationToken);
    }

    internal static Task<AnalyzerAction> GetAnalyzerActionAsync(Guid id, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSeasonStateStore().GetAnalyzerActionAsync(id, mode, cancellationToken);
    }

    internal static Task CleanSeasonStateAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSeasonStateStore().CleanSeasonStatesAsync(ids as IReadOnlyCollection<Guid> ?? [.. ids], cancellationToken);
    }

    internal static AnalysisMode MapSegmentTypeToMode(MediaSegmentType type)
    {
        return type switch
        {
            MediaSegmentType.Intro => AnalysisMode.Introduction,
            MediaSegmentType.Recap => AnalysisMode.Recap,
            MediaSegmentType.Preview => AnalysisMode.Preview,
            MediaSegmentType.Outro => AnalysisMode.Credits,
            MediaSegmentType.Commercial => AnalysisMode.Commercial,
            _ => throw new NotImplementedException(),
        };
    }

    /// <summary>
    /// Deletes a stored timestamp (DbSegment) for the specified item and analysis mode.
    /// </summary>
    /// <param name="itemId">The item id whose timestamp should be removed.</param>
    /// <param name="mode">The analysis mode representing the segment type.</param>
    /// <param name="segment">Optional segment details used to remove a specific entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static Task DeleteTimestampAsync(
        Guid itemId,
        AnalysisMode mode,
        Segment? segment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance.CreateSegmentUpdateService().DeleteTimestampAsync(itemId, mode, segment, cancellationToken);
    }

    private SegmentStore CreateSegmentStore() => new(new SegmentDbContextFactory(_dbPath));

    private SeasonStateStore CreateSeasonStateStore() => new(new SegmentDbContextFactory(_dbPath));

    private SegmentUpdateService CreateSegmentUpdateService() => new(CreateSegmentStore(), (ILogger?)_logger ?? NullLogger.Instance);

    private void MigrateLegacyExcludeSeries()
    {
        var legacy = Configuration.ExcludeSeries;
        if (string.IsNullOrWhiteSpace(legacy))
        {
            return;
        }

        if (Configuration.SeriesExclusions.Count == 0)
        {
            foreach (var item in legacy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Configuration.SeriesExclusions.Add(item);
            }
        }

        Configuration.ExcludeSeries = string.Empty;
        SaveConfiguration();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing database")]
    private static partial void LogDatabaseInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing detection cache database")]
    private static partial void LogCacheDbInitializationError(ILogger logger, Exception exception);
}
