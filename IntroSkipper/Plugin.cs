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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper;

/// <summary>
/// Intro skipper plugin. Uses audio analysis to find common sequences of audio shared between episodes.
/// </summary>
public partial class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private const double SegmentComparisonEpsilon = 0.001;
    private const int SqliteParameterBatchSize = 500;
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

        var pluginDirName = "introskipper";
        var pluginCachePath = "chromaprints";

        var introsDirectory = Path.Join(applicationPaths.DataPath, pluginDirName);
        FingerprintCachePath = Path.Join(introsDirectory, pluginCachePath);

        _dbPath = Path.Join(introsDirectory, "introskipper.db");
        _cacheDbPath = Path.Join(introsDirectory, "introskipper-cache.db");

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

    internal async Task UpdateTimestampAsync(
        Segment segment,
        AnalysisMode mode,
        bool isUserProvided = false,
        string configHash = "",
        CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();

        try
        {
            var dbSegment = new DbSegment(segment, mode, isUserProvided, configHash);

            if (mode == AnalysisMode.Commercial)
            {
                var exists = await db.DbSegment
                    .AnyAsync(
                        s => s.ItemId == segment.EpisodeId
                             && s.Type == mode
                             && Math.Abs(s.Start - dbSegment.Start) <= SegmentComparisonEpsilon
                             && Math.Abs(s.End - dbSegment.End) <= SegmentComparisonEpsilon,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!exists)
                {
                    db.DbSegment.Add(dbSegment);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var existingSegments = await db.DbSegment
                        .Where(s => s.ItemId == segment.EpisodeId && s.Type == mode)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    // Do not overwrite a user-provided segment with an analysis result.
                    if (!isUserProvided && existingSegments.Any(s => s.IsUserProvided))
                    {
                        return;
                    }

                    // Guard: prevent auto-detected credits from overlapping with the introduction.
                    if (mode == AnalysisMode.Credits && !isUserProvided)
                    {
                        var intro = await db.DbSegment
                            .AsNoTracking()
                            .Where(s => s.ItemId == segment.EpisodeId && s.Type == AnalysisMode.Introduction)
                            .FirstOrDefaultAsync(cancellationToken)
                            .ConfigureAwait(false);

                        if (intro is not null && segment.Start < intro.End && intro.Start < segment.End)
                        {
                            LogCreditsOverlapWithIntro(_logger, segment.EpisodeId);
                            return;
                        }
                    }

                    if (existingSegments.Count > 0)
                    {
                        db.DbSegment.RemoveRange(existingSegments);
                    }

                    db.DbSegment.Add(dbSegment);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            LogFailedToUpdateTimestamp(_logger, ex, segment.EpisodeId);
            throw;
        }
    }

    internal static async Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var segments = await db.DbSegment
            .AsNoTracking()
            .Where(s => s.ItemId == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return segments
            .GroupBy(segment => segment.Type)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(segment => segment.Start).First().ToSegment());
    }

    internal static async Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        return await db.DbSegment
            .AsNoTracking()
            .Where(s => s.ItemId == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<IReadOnlyCollection<Guid>> CleanTimestampsAsync(IEnumerable<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        var enabledEpisodeIds = episodeIds.ToHashSet();

        using var db = CreateDbContext();
        var segmentEpisodeIds = await db.DbSegment
            .AsNoTracking()
            .Select(s => s.ItemId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var staleEpisodeIds = segmentEpisodeIds
            .Where(id => !enabledEpisodeIds.Contains(id))
            .ToArray();

        foreach (var staleEpisodeIdBatch in staleEpisodeIds.Chunk(SqliteParameterBatchSize))
        {
            await db.DbSegment
                .Where(s => staleEpisodeIdBatch.Contains(s.ItemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return staleEpisodeIds;
    }

    internal static async Task SetAnalyzerActionAsync(Guid id, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var existingEntries = await db.DbSeasonState
            .Where(s => s.SeasonId == id)
            .ToDictionaryAsync(s => s.Type, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (mode, action) in analyzerActions)
        {
            if (existingEntries.TryGetValue(mode, out var existing))
            {
                db.Entry(existing).Property(s => s.Action).CurrentValue = action;
            }
            else
            {
                db.DbSeasonState.Add(new DbSeasonState(id, mode, action));
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SetEpisodeIdsAsync(Guid id, AnalysisMode mode, IEnumerable<Guid> episodeIds, string configHash = "", CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var seasonState = await db.DbSeasonState
            .FirstOrDefaultAsync(s => s.SeasonId == id && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        if (seasonState is null)
        {
            seasonState = new DbSeasonState(id, mode, AnalyzerAction.Default, episodeIds, configHash);
            db.DbSeasonState.Add(seasonState);
        }
        else
        {
            db.Entry(seasonState).Property(s => s.EpisodeIds).CurrentValue = episodeIds;
            db.Entry(seasonState).Property(s => s.ConfigHash).CurrentValue = configHash;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a single episode ID from the season's analyzed-state list for the given mode.
    /// The read and write share one <see cref="IntroSkipperDbContext"/> to keep the window for
    /// concurrent overwrites as small as possible, and the write is skipped entirely when the
    /// ID is not present in the stored list.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="episodeId">Episode ID to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task RemoveEpisodeIdAsync(Guid seasonId, AnalysisMode mode, Guid episodeId, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var seasonState = await db.DbSeasonState
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        if (seasonState is null)
        {
            return;
        }

        var currentIds = seasonState.EpisodeIds.ToList();
        if (!currentIds.Remove(episodeId))
        {
            return; // Episode was not in the list — no write needed.
        }

        db.Entry(seasonState).Property(s => s.EpisodeIds).CurrentValue = currentIds;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
    internal static async Task CleanStaleAutomaticSegmentsAsync(
        IEnumerable<Guid> itemIds,
        AnalysisMode mode,
        string configHash,
        CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        using var db = CreateDbContext();
        foreach (var batch in ids.Chunk(SqliteParameterBatchSize))
        {
            await db.DbSegment
                .Where(s => batch.Contains(s.ItemId)
                    && s.Type == mode
                    && !s.IsUserProvided
                    && s.ConfigHash != configHash)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static async Task<IReadOnlyDictionary<AnalysisMode, IEnumerable<Guid>>> GetEpisodeIdsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        return await db.DbSeasonState.Where(s => s.SeasonId == id)
            .ToDictionaryAsync(s => s.Type, s => s.EpisodeIds, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the settled-season reanalysis state for all modes in a season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Settled reanalysis state keyed by analysis mode.</returns>
    internal static async Task<IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>> GetSettleReanalysisStatesAsync(
        Guid seasonId,
        CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var states = await db.DbSeasonState
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .Select(s => new
            {
                s.Type,
                s.Action,
                s.SettledReanalysisEpisodeIds
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return states.ToDictionary(
            s => s.Type,
            s => (s.Action, (IReadOnlySet<Guid>)s.SettledReanalysisEpisodeIds.ToHashSet()));
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
    internal static async Task RecordSettleReanalysisAsync(
        Guid seasonId,
        IReadOnlyCollection<AnalysisMode> modes,
        IReadOnlyCollection<Guid> episodeIds,
        CancellationToken cancellationToken = default)
    {
        if (modes.Count == 0)
        {
            return;
        }

        var settledEpisodeIds = DbSeasonState.SerializeEpisodeIds(episodeIds);

        using var db = CreateDbContext();
        foreach (var mode in modes)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "DbSeasonState" ("SeasonId", "Type", "Action", "EpisodeIds", "ConfigHash", "SettledReanalysisEpisodeIds")
                VALUES ({seasonId}, {(int)mode}, {(int)AnalyzerAction.Default}, {"[]"}, {string.Empty}, {settledEpisodeIds})
                ON CONFLICT("SeasonId", "Type") DO UPDATE SET
                    "SettledReanalysisEpisodeIds" = excluded."SettledReanalysisEpisodeIds"
                """,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clears stored automatic analysis state for a season so it is re-analyzed from scratch on the
    /// current pass. Automatic segments for the supplied modes are deleted and the season's analyzed
    /// episode lists are cleared; user-provided segments and the fingerprint cache are preserved.
    /// The deletes and the episode-list clear run in a single transaction so a cancelled reset cannot
    /// leave segments deleted while their episodes are still recorded as analyzed.
    /// </summary>
    /// <param name="seasonId">Season ID whose analyzed-state lists should be cleared.</param>
    /// <param name="episodeIds">Episode IDs whose automatic segments should be removed.</param>
    /// <param name="modes">Analysis modes to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task ResetSeasonForReanalysisAsync(
        Guid seasonId,
        IEnumerable<Guid> episodeIds,
        IReadOnlyCollection<AnalysisMode> modes,
        CancellationToken cancellationToken = default)
    {
        var ids = episodeIds.Distinct().ToArray();
        var modeArray = modes.ToArray();
        if (ids.Length == 0 || modeArray.Length == 0)
        {
            return;
        }

        using var db = CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var batch in ids.Chunk(SqliteParameterBatchSize))
            {
                await db.DbSegment
                    .Where(s => batch.Contains(s.ItemId) && modeArray.Contains(s.Type) && !s.IsUserProvided)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            // Clear the analyzed-episode lists so VerifyQueueAsync treats every episode as NotAnalyzed.
            // Committing this together with the deletes guarantees the episodes are re-analyzed (either
            // on this pass or a later one) instead of being stranded as NoSegments.
            var seasonStates = await db.DbSeasonState
                .Where(s => s.SeasonId == seasonId && modeArray.Contains(s.Type))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var state in seasonStates)
            {
                db.Entry(state).Property(s => s.EpisodeIds).CurrentValue = [];
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static async Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var episodeIdArray = (Guid[])[.. episodeIds.Distinct()];

        var seasonStates = await db.DbSeasonState
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var allSegments = new List<DbSegment>();
        foreach (var batch in episodeIdArray.Chunk(SqliteParameterBatchSize))
        {
            allSegments.AddRange(await db.DbSegment
                .AsNoTracking()
                .Where(s => batch.Contains(s.ItemId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        var segments = allSegments;

        return new SeasonQueueSnapshot(
            seasonStates.ToDictionary(s => s.Type, s => (IReadOnlySet<Guid>)s.EpisodeIds.ToHashSet()),
            seasonStates.ToDictionary(s => s.Type, s => s.ConfigHash),
            seasonStates.ToDictionary(s => s.Type, s => s.Action),
            segments
                .GroupBy(s => s.ItemId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyDictionary<AnalysisMode, Segment>)group
                        .GroupBy(segment => segment.Type)
                        .ToDictionary(
                            segmentGroup => segmentGroup.Key,
                            segmentGroup => segmentGroup.OrderBy(segment => segment.Start).First().ToSegment())),
            segments
                .Where(s => s.IsUserProvided)
                .GroupBy(s => s.Type)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlySet<Guid>)group.Select(s => s.ItemId).ToHashSet()));
    }

    internal static async Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var states = await db.DbSeasonState
            .Where(s => s.SeasonId == seasonId)
            .ToDictionaryAsync(s => s.Type, s => s.Action, cancellationToken)
            .ConfigureAwait(false);

        // Fill in defaults for any missing modes
        var result = new Dictionary<AnalysisMode, AnalyzerAction>();
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            result[mode] = states.TryGetValue(mode, out var action) ? action : AnalyzerAction.Default;
        }

        return result;
    }

    internal static async Task<AnalyzerAction> GetAnalyzerActionAsync(Guid id, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var state = await db.DbSeasonState
            .FirstOrDefaultAsync(s => s.SeasonId == id && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);
        return state?.Action ?? AnalyzerAction.Default;
    }

    internal static async Task CleanSeasonStateAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        await db.DbSeasonState
            .Where(s => !ids.Contains(s.SeasonId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
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
    internal static async Task DeleteTimestampAsync(
        Guid itemId,
        AnalysisMode mode,
        Segment? segment = null,
        CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        if (segment is null && mode == AnalysisMode.Commercial)
        {
            return;
        }

        var query = db.DbSegment.Where(s => s.ItemId == itemId && s.Type == mode);

        if (segment is not null)
        {
            query = query.Where(s =>
                Math.Abs(s.Start - segment.Start) <= SegmentComparisonEpsilon
                && Math.Abs(s.End - segment.End) <= SegmentComparisonEpsilon);
        }

        var entries = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (entries.Count > 0)
        {
            db.DbSegment.RemoveRange(entries);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing database")]
    private static partial void LogDatabaseInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing detection cache database")]
    private static partial void LogCacheDbInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping credits for episode {EpisodeId}: detected segment overlaps with introduction")]
    private static partial void LogCreditsOverlapWithIntro(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update timestamp for episode {EpisodeId}")]
    private static partial void LogFailedToUpdateTimestamp(ILogger logger, Exception ex, Guid episodeId);
}
