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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper;

/// <summary>
/// Intro skipper plugin. Uses audio analysis to find common sequences of audio shared between episodes.
/// </summary>
/// <remarks>
/// Database access lives in <see cref="IntroSkipper.Db"/> (context extension methods plus
/// <see cref="DatabaseInitializer"/>). The DB members that remain on this class are thin
/// delegations kept only for call sites that are not yet constructor-injected; new code should
/// inject <see cref="IDbContextFactory{TContext}"/> directly.
/// </remarks>
public partial class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILibraryManager _libraryManager;
    private readonly IChapterManager _chapterRepository;
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<Plugin> _logger;
    private readonly IDbContextFactory<IntroSkipperDbContext>? _dbContextFactory;
    private readonly IDbContextFactory<DetectionCacheDbContext>? _cacheDbContextFactory;
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
    /// <param name="dbContextFactory">Factory for the segment database context.</param>
    /// <param name="cacheDbContextFactory">Factory for the detection cache database context.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServerConfigurationManager serverConfiguration,
        ILibraryManager libraryManager,
        IChapterManager chapterRepository,
        IPluginManager pluginManager,
        ILogger<Plugin> logger,
        IDbContextFactory<IntroSkipperDbContext> dbContextFactory,
        IDbContextFactory<DetectionCacheDbContext> cacheDbContextFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        _libraryManager = libraryManager;
        _chapterRepository = chapterRepository;
        _pluginManager = pluginManager;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _cacheDbContextFactory = cacheDbContextFactory;

        FFmpegPath = serverConfiguration.GetEncodingOptions().EncoderAppPathDisplay;

        ArgumentNullException.ThrowIfNull(applicationPaths);

        var pluginCachePath = "chromaprints";

        var introsDirectory = IntroSkipperDatabase.GetPluginDirectory(applicationPaths);
        FingerprintCachePath = Path.Join(introsDirectory, pluginCachePath);

        _dbPath = IntroSkipperDatabase.GetSegmentDatabasePath(applicationPaths);
        _cacheDbPath = IntroSkipperDatabase.GetCacheDatabasePath(applicationPaths);

        // Create the base directories (if needed).
        // Directory.CreateDirectory is already a no-op when the directory exists, so we can call it unconditionally without checking first.
        Directory.CreateDirectory(introsDirectory);

        // Database initialization (migrations, legacy repair, cache recovery) is owned by
        // DatabaseInitializer. It runs eagerly via DatabaseInitializationService and is awaited
        // lazily by the gated context factories, so no query can run before it completes.
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
    /// Delegates to the DI-provided gated factory (which awaits database initialization); test
    /// instances materialized without DI fall back to a path-based context.
    /// </summary>
    /// <returns>A new <see cref="IntroSkipperDbContext"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the plugin has not been initialized.</exception>
    public static IntroSkipperDbContext CreateDbContext()
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance._dbContextFactory is { } factory
            ? factory.CreateDbContext()
            : new IntroSkipperDbContext(Instance.DbPath);
    }

    /// <summary>
    /// Creates a new <see cref="DetectionCacheDbContext"/> instance configured for the plugin cache database.
    /// Delegates to the DI-provided gated factory (which awaits database initialization); test
    /// instances materialized without DI fall back to a path-based context.
    /// </summary>
    /// <returns>A new <see cref="DetectionCacheDbContext"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the plugin has not been initialized.</exception>
    public static DetectionCacheDbContext CreateCacheDbContext()
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance._cacheDbContextFactory is { } factory
            ? factory.CreateDbContext()
            : new DetectionCacheDbContext(Instance.CacheDbPath);
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

    internal async Task UpdateTimestampAsync(
        Segment segment,
        AnalysisMode mode,
        bool isUserProvided = false,
        string configHash = "",
        CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.UpdateTimestampAsync(segment, mode, isUserProvided, configHash, _logger, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.GetTimestampsAsync(id, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.GetSegmentsAsync(id, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task DeleteItemSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.DeleteItemSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CleanTimestampsAsync(IEnumerable<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.CleanTimestampsAsync(episodeIds, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SetAnalyzerActionAsync(Guid id, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.SetAnalyzerActionAsync(id, analyzerActions, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SetEpisodeIdsAsync(Guid id, AnalysisMode mode, IEnumerable<Guid> episodeIds, string configHash = "", CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.SetEpisodeIdsAsync(id, mode, episodeIds, configHash, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RemoveEpisodeIdAsync(Guid seasonId, AnalysisMode mode, Guid episodeId, CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.RemoveEpisodeIdAsync(seasonId, mode, episodeId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CleanStaleAutomaticSegmentsAsync(
        IEnumerable<Guid> itemIds,
        AnalysisMode mode,
        string configHash,
        CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.CleanStaleAutomaticSegmentsAsync(itemIds, mode, configHash, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyDictionary<AnalysisMode, IEnumerable<Guid>>> GetEpisodeIdsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.GetEpisodeIdsAsync(id, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>> GetSettleReanalysisStatesAsync(
        Guid seasonId,
        CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.GetSettleReanalysisStatesAsync(seasonId, cancellationToken).ConfigureAwait(false);
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

    internal static async Task RecordSettleReanalysisAsync(
        Guid seasonId,
        IReadOnlyCollection<AnalysisMode> modes,
        IReadOnlyCollection<Guid> episodeIds,
        CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.RecordSettleReanalysisAsync(seasonId, modes, episodeIds, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ResetSeasonForReanalysisAsync(
        Guid seasonId,
        IEnumerable<Guid> episodeIds,
        IReadOnlyCollection<AnalysisMode> modes,
        CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.ResetSeasonForReanalysisAsync(seasonId, episodeIds, modes, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.GetSeasonQueueSnapshotAsync(seasonId, episodeIds, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.GetAllAnalyzerActionsAsync(seasonId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<AnalyzerAction> GetAnalyzerActionAsync(Guid id, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.GetAnalyzerActionAsync(id, mode, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CleanSeasonStateAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.CleanSeasonStateAsync(ids, cancellationToken).ConfigureAwait(false);
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

    internal static async Task DeleteTimestampAsync(
        Guid itemId,
        AnalysisMode mode,
        Segment? segment = null,
        CancellationToken cancellationToken = default)
    {
        using var db = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.DeleteTimestampAsync(itemId, mode, segment, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IntroSkipperDbContext> CreateDbContextAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return Instance._dbContextFactory is { } factory
            ? await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false)
            : new IntroSkipperDbContext(Instance.DbPath);
    }

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
}
