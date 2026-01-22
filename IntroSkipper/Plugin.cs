// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
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
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
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
            using var db = new IntroSkipperDbContext(_dbPath);
            db.ApplyMigrations();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Error initializing database: {Exception}", ex);
        }

        FileTransformationPluginEnabled = _pluginManager
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

    /// <summary>
    /// Gets a value indicating whether the File Transformation plugin is enabled.
    /// </summary>
    public bool FileTransformationPluginEnabled { get; private set; }

    /// <inheritdoc />
    public override string Name => "Intro Skipper";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c83d86bb-a1e0-4c35-a113-e2101cf4ee6b");

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

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

    internal async Task UpdateTimestampAsync(Segment segment, AnalysisMode mode)
    {
        using var db = new IntroSkipperDbContext(_dbPath);

        try
        {
            var existing = await db.DbSegment
                .FirstOrDefaultAsync(s => s.ItemId == segment.EpisodeId && s.Type == mode)
                .ConfigureAwait(false);

            var dbSegment = new DbSegment(segment, mode);
            if (existing is not null)
            {
                db.Entry(existing).CurrentValues.SetValues(dbSegment);
            }
            else
            {
                db.DbSegment.Add(dbSegment);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update timestamp for episode {EpisodeId}", segment.EpisodeId);
            throw;
        }
    }

    internal IReadOnlyDictionary<AnalysisMode, Segment> GetTimestamps(Guid id)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        return db.DbSegment.Where(s => s.ItemId == id)
            .ToDictionary(s => s.Type, s => s.ToSegment());
    }

    internal async Task CleanTimestamps(IEnumerable<Guid> episodeIds)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        db.DbSegment.RemoveRange(db.DbSegment
            .Where(s => !episodeIds.Contains(s.ItemId)));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    internal async Task SetAnalyzerActionAsync(Guid id, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        var existingEntries = await db.DbSeasonInfo
            .Where(s => s.SeasonId == id)
            .ToDictionaryAsync(s => s.Type)
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

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    internal async Task SetEpisodeIdsAsync(Guid id, AnalysisMode mode, IEnumerable<Guid> episodeIds)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        var seasonInfo = db.DbSeasonInfo.FirstOrDefault(s => s.SeasonId == id && s.Type == mode);

        if (seasonInfo is null)
        {
            seasonInfo = new DbSeasonInfo(id, mode, AnalyzerAction.Default, episodeIds);
            db.DbSeasonInfo.Add(seasonInfo);
        }
        else
        {
            db.Entry(seasonInfo).Property(s => s.EpisodeIds).CurrentValue = episodeIds;
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    internal IReadOnlyDictionary<AnalysisMode, IEnumerable<Guid>> GetEpisodeIds(Guid id)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        return db.DbSeasonInfo.Where(s => s.SeasonId == id)
            .ToDictionary(s => s.Type, s => s.EpisodeIds);
    }

    internal AnalyzerAction GetAnalyzerAction(Guid id, AnalysisMode mode)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        return db.DbSeasonInfo.FirstOrDefault(s => s.SeasonId == id && s.Type == mode)?.Action ?? AnalyzerAction.Default;
    }

    internal async Task CleanSeasonInfoAsync(IEnumerable<Guid> ids)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        var obsoleteSeasons = await db.DbSeasonInfo
            .Where(s => !ids.Contains(s.SeasonId))
            .ToListAsync().ConfigureAwait(false);
        db.DbSeasonInfo.RemoveRange(obsoleteSeasons);
        await db.SaveChangesAsync().ConfigureAwait(false);
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
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal async Task DeleteTimestampAsync(Guid itemId, AnalysisMode mode)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        var entry = await db.DbSegment.FirstOrDefaultAsync(s => s.ItemId == itemId && s.Type == mode).ConfigureAwait(false);
        if (entry is not null)
        {
            db.DbSegment.Remove(entry);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }
}
