// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
public partial class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private const double SegmentComparisonEpsilon = 0.001;
    private readonly ILibraryManager _libraryManager;
    private readonly IChapterManager _chapterRepository;
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<Plugin> _logger;
    private readonly string _dbPath;

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

        _dbPath = Path.Join(applicationPaths.DataPath, pluginDirName, "introskipper.db");

        // Create the base & cache directories (if needed).
        if (!Directory.Exists(FingerprintCachePath))
        {
            Directory.CreateDirectory(FingerprintCachePath);
        }

        // Initialize database, restore timestamps if available.
        try
        {
            using var db = CreateDbContext();
            db.ApplyMigrations();
        }
        catch (Exception ex)
        {
            LogDatabaseInitializationError(_logger, ex);
        }

        Configuration.FileTransformationPluginEnabled = _pluginManager
            .Plugins
            .Any(p => p.Id == Guid.Parse("5e87cc92-571a-4d8d-8d98-d2d4147f9f90")); // File Transformation plugin ID
    }

    /// <summary>
    /// Gets the path to the database.
    /// </summary>
    public string DbPath => _dbPath;

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
            }
        ];
    }

    internal BaseItem? GetItem(Guid id) => id != Guid.Empty ? _libraryManager.GetItemById(id) : null;

    internal ICollection<Folder> GetCollectionFolders(Guid id) => GetItem(id) is var item && item is not null ? _libraryManager.GetCollectionFolders(item) : [];

    internal string GetItemPath(Guid id) => GetItem(id) is var item && item is not null ? item.Path : string.Empty;

    internal IReadOnlyList<ChapterInfo> GetChapters(Guid id) => _chapterRepository.GetChapters(id);

    internal async Task UpdateTimestampAsync(Segment segment, AnalysisMode mode, bool isUserProvided = false, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();

        try
        {
            var dbSegment = new DbSegment(segment, mode, isUserProvided);

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

    internal async Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(Guid id, CancellationToken cancellationToken = default)
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

    internal async Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        return await db.DbSegment
            .AsNoTracking()
            .Where(s => s.ItemId == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task CleanTimestampsAsync(IEnumerable<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        await db.DbSegment
            .Where(s => !episodeIds.Contains(s.ItemId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task SetAnalyzerActionAsync(Guid id, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var existingEntries = await db.DbSeasonInfo
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
                db.DbSeasonInfo.Add(new DbSeasonInfo(id, mode, action));
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task SetEpisodeIdsAsync(Guid id, AnalysisMode mode, IEnumerable<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var seasonInfo = await db.DbSeasonInfo
            .FirstOrDefaultAsync(s => s.SeasonId == id && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        if (seasonInfo is null)
        {
            seasonInfo = new DbSeasonInfo(id, mode, AnalyzerAction.Default, episodeIds);
            db.DbSeasonInfo.Add(seasonInfo);
        }
        else
        {
            db.Entry(seasonInfo).Property(s => s.EpisodeIds).CurrentValue = episodeIds;
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
    internal async Task RemoveEpisodeIdAsync(Guid seasonId, AnalysisMode mode, Guid episodeId, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var seasonInfo = await db.DbSeasonInfo
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        if (seasonInfo is null)
        {
            return;
        }

        var currentIds = seasonInfo.EpisodeIds.ToList();
        if (!currentIds.Remove(episodeId))
        {
            return; // Episode was not in the list — no write needed.
        }

        db.Entry(seasonInfo).Property(s => s.EpisodeIds).CurrentValue = currentIds;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyDictionary<AnalysisMode, IEnumerable<Guid>>> GetEpisodeIdsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        return await db.DbSeasonInfo.Where(s => s.SeasonId == id)
            .ToDictionaryAsync(s => s.Type, s => s.EpisodeIds, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var episodeIdArray = (Guid[])[.. episodeIds.Distinct()];

        var seasonInfos = await db.DbSeasonInfo
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var segments = episodeIdArray.Length == 0
            ? []
            : await db.DbSegment
                .AsNoTracking()
                .Where(s => episodeIdArray.Contains(s.ItemId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        return new SeasonQueueSnapshot(
            seasonInfos.ToDictionary(s => s.Type, s => (IReadOnlySet<Guid>)s.EpisodeIds.ToHashSet()),
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

    internal async Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var infos = await db.DbSeasonInfo
            .Where(s => s.SeasonId == seasonId)
            .ToDictionaryAsync(s => s.Type, s => s.Action, cancellationToken)
            .ConfigureAwait(false);

        // Fill in defaults for any missing modes
        var result = new Dictionary<AnalysisMode, AnalyzerAction>();
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            result[mode] = infos.TryGetValue(mode, out var action) ? action : AnalyzerAction.Default;
        }

        return result;
    }

    internal async Task<AnalyzerAction> GetAnalyzerActionAsync(Guid id, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var info = await db.DbSeasonInfo
            .FirstOrDefaultAsync(s => s.SeasonId == id && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);
        return info?.Action ?? AnalyzerAction.Default;
    }

    internal async Task CleanSeasonInfoAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        await db.DbSeasonInfo
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
    internal async Task DeleteTimestampAsync(
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing database")]
    private static partial void LogDatabaseInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update timestamp for episode {EpisodeId}")]
    private static partial void LogFailedToUpdateTimestamp(ILogger logger, Exception ex, Guid episodeId);
}
