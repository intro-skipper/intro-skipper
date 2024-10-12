using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ConfusedPolarBear.Plugin.IntroSkipper.Configuration;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Intro skipper plugin. Uses audio analysis to find common sequences of audio shared between episodes.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly object _serializationLock = new();
    private readonly object _introsLock = new();
    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _itemRepository;
    private readonly IApplicationHost _applicationHost;
    private readonly ILogger<Plugin> _logger;
    private readonly string _introPath;
    private readonly string _creditsPath;
    private string _ignorelistPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationHost">Application host.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="serverConfiguration">Server configuration manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="itemRepository">Item repository.</param>
    /// <param name="logger">Logger.</param>
    public Plugin(
        IApplicationHost applicationHost,
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServerConfigurationManager serverConfiguration,
        ILibraryManager libraryManager,
        IItemRepository itemRepository,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        _applicationHost = applicationHost;
        _libraryManager = libraryManager;
        _itemRepository = itemRepository;
        _logger = logger;

        FFmpegPath = serverConfiguration.GetEncodingOptions().EncoderAppPathDisplay;

        ArgumentNullException.ThrowIfNull(applicationPaths);

        var pluginDirName = "introskipper";
        var pluginCachePath = "chromaprints";

        var introsDirectory = Path.Join(applicationPaths.DataPath, pluginDirName);
        FingerprintCachePath = Path.Join(introsDirectory, pluginCachePath);
        _introPath = Path.Join(applicationPaths.DataPath, pluginDirName, "intros.xml");
        _creditsPath = Path.Join(applicationPaths.DataPath, pluginDirName, "credits.xml");
        _ignorelistPath = Path.Join(applicationPaths.DataPath, pluginDirName, "ignorelist.xml");

        // Create the base & cache directories (if needed).
        if (!Directory.Exists(FingerprintCachePath))
        {
            Directory.CreateDirectory(FingerprintCachePath);
        }

        // migrate from XMLSchema to DataContract
        XmlSerializationHelper.MigrateXML(_introPath);
        XmlSerializationHelper.MigrateXML(_creditsPath);

        MigrateRepoUrl(serverConfiguration);

        // TODO: remove when https://github.com/jellyfin/jellyfin-meta/discussions/30 is complete
        try
        {
            RestoreTimestamps();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Unable to load introduction timestamps: {Exception}", ex);
        }

        try
        {
            LoadIgnoreList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Unable to load ignore list: {Exception}", ex);
        }

        // Inject the skip intro button code into the web interface.
        try
        {
            InjectSkipButton(applicationPaths.WebPath);
        }
        catch (Exception ex)
        {
            WarningManager.SetFlag(PluginWarning.UnableToAddSkipButton);

            _logger.LogError("Failed to add skip button to web interface. See https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting#skip-button-is-not-visible for the most common issues. Error: {Error}", ex);
        }

        FFmpegWrapper.CheckFFmpegVersion();
    }

    /// <summary>
    /// Gets the results of fingerprinting all episodes.
    /// </summary>
    public ConcurrentDictionary<Guid, Segment> Intros { get; } = new();

    /// <summary>
    /// Gets all discovered ending credits.
    /// </summary>
    public ConcurrentDictionary<Guid, Segment> Credits { get; } = new();

    /// <summary>
    /// Gets the most recent media item queue.
    /// </summary>
    public ConcurrentDictionary<Guid, List<QueuedEpisode>> QueuedMediaItems { get; } = new();

    /// <summary>
    /// Gets all episode states.
    /// </summary>
    public ConcurrentDictionary<Guid, EpisodeState> EpisodeStates { get; } = new();

    /// <summary>
    /// Gets the ignore list.
    /// </summary>
    public ConcurrentDictionary<Guid, IgnoreListItem> IgnoreList { get; } = new();

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
    /// Save timestamps to disk.
    /// </summary>
    /// <param name="mode">Mode.</param>
    public void SaveTimestamps(AnalysisMode mode)
    {
        List<Segment> introList = [];
        var filePath = mode == AnalysisMode.Introduction
                        ? _introPath
                        : _creditsPath;

        lock (_introsLock)
        {
            introList.AddRange(mode == AnalysisMode.Introduction
                            ? Instance!.Intros.Values
                            : Instance!.Credits.Values);
        }

        lock (_serializationLock)
        {
            try
            {
                XmlSerializationHelper.SerializeToXml(introList, filePath);
            }
            catch (Exception e)
            {
                _logger.LogError("SaveTimestamps {Message}", e.Message);
            }
        }
    }

    /// <summary>
    /// Save IgnoreList to disk.
    /// </summary>
    public void SaveIgnoreList()
    {
        var ignorelist = Instance!.IgnoreList.Values.ToList();

        lock (_serializationLock)
        {
            try
            {
                XmlSerializationHelper.SerializeToXml(ignorelist, _ignorelistPath);
            }
            catch (Exception e)
            {
                _logger.LogError("SaveIgnoreList {Message}", e.Message);
            }
        }
    }

    /// <summary>
    /// Check if an item is ignored.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <param name="mode">Mode.</param>
    /// <returns>True if ignored, false otherwise.</returns>
    public bool IsIgnored(Guid id, AnalysisMode mode)
    {
        return Instance!.IgnoreList.TryGetValue(id, out var item) && item.IsIgnored(mode);
    }

    /// <summary>
    /// Load IgnoreList from disk.
    /// </summary>
    public void LoadIgnoreList()
    {
        if (File.Exists(_ignorelistPath))
        {
            var ignorelist = XmlSerializationHelper.DeserializeFromXml<IgnoreListItem>(_ignorelistPath);

            foreach (var item in ignorelist)
            {
                Instance!.IgnoreList.TryAdd(item.SeasonId, item);
            }
        }
    }

    /// <summary>
    /// Restore previous analysis results from disk.
    /// </summary>
    public void RestoreTimestamps()
    {
        if (File.Exists(_introPath))
        {
            // Since dictionaries can't be easily serialized, analysis results are stored on disk as a list.
            var introList = XmlSerializationHelper.DeserializeFromXml<Segment>(_introPath);

            foreach (var intro in introList)
            {
                Instance!.Intros.TryAdd(intro.EpisodeId, intro);
            }
        }

        if (File.Exists(_creditsPath))
        {
            var creditList = XmlSerializationHelper.DeserializeFromXml<Segment>(_creditsPath);

            foreach (var credit in creditList)
            {
                Instance!.Credits.TryAdd(credit.EpisodeId, credit);
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "visualizer.js",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.visualizer.js"
            },
            new PluginPageInfo
            {
                Name = "skip-intro-button.js",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.inject.js"
            }
        ];
    }

    /// <summary>
    /// Gets the Intro for this item.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <param name="mode">Mode.</param>
    /// <returns>Intro.</returns>
    internal static Segment GetIntroByMode(Guid id, AnalysisMode mode)
    {
        return mode == AnalysisMode.Introduction
            ? Instance!.Intros[id]
            : Instance!.Credits[id];
    }

    internal BaseItem? GetItem(Guid id)
    {
        return id != Guid.Empty ? _libraryManager.GetItemById(id) : null;
    }

    internal IReadOnlyList<Folder> GetCollectionFolders(Guid id)
    {
        var item = GetItem(id);
        return item is not null ? _libraryManager.GetCollectionFolders(item) : [];
    }

    /// <summary>
    /// Gets the full path for an item.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <returns>Full path to item.</returns>
    internal string GetItemPath(Guid id)
    {
        var item = GetItem(id);
        if (item == null)
        {
            // Handle the case where the item is not found
            _logger.LogWarning("Item with ID {Id} not found.", id);
            return string.Empty;
        }

        return item.Path;
    }

    /// <summary>
    /// Gets all chapters for this item.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <returns>List of chapters.</returns>
    internal IReadOnlyList<ChapterInfo> GetChapters(Guid id)
    {
        var item = GetItem(id);
        if (item == null)
        {
            // Handle the case where the item is not found
            _logger.LogWarning("Item with ID {Id} not found.", id);
            return [];
        }

        return _itemRepository.GetChapters(item);
    }

    /// <summary>
    /// Gets the state for this item.
    /// </summary>
    /// <param name="id">Item ID.</param>
    /// <returns>State of this item.</returns>
    internal EpisodeState GetState(Guid id) => EpisodeStates.GetOrAdd(id, _ => new EpisodeState());

    internal void UpdateTimestamps(IReadOnlyDictionary<Guid, Segment> newTimestamps, AnalysisMode mode)
    {
        foreach (var intro in newTimestamps)
        {
            if (mode == AnalysisMode.Introduction)
            {
                Instance!.Intros.AddOrUpdate(intro.Key, intro.Value, (key, oldValue) => intro.Value);
            }
            else if (mode == AnalysisMode.Credits)
            {
                Instance!.Credits.AddOrUpdate(intro.Key, intro.Value, (key, oldValue) => intro.Value);
            }
        }

        SaveTimestamps(mode);
    }

    internal void CleanTimestamps(HashSet<Guid> validEpisodeIds)
    {
        var allKeys = new HashSet<Guid>(Instance!.Intros.Keys);
        allKeys.UnionWith(Instance!.Credits.Keys);

        foreach (var key in allKeys)
        {
            if (!validEpisodeIds.Contains(key))
            {
                Instance!.Intros.TryRemove(key, out _);
                Instance!.Credits.TryRemove(key, out _);
            }
        }

        SaveTimestamps(AnalysisMode.Introduction);
        SaveTimestamps(AnalysisMode.Credits);
    }

    private void MigrateRepoUrl(IServerConfigurationManager serverConfiguration)
    {
        try
        {
            List<string> oldRepos =
            [
            "https://raw.githubusercontent.com/intro-skipper/intro-skipper/master/manifest.json",
            "https://raw.githubusercontent.com/jumoog/intro-skipper/master/manifest.json"
            ];
            // Access the current server configuration
            var config = serverConfiguration.Configuration;

            // Get the list of current plugin repositories
            var pluginRepositories = config.PluginRepositories?.ToList() ?? [];

            // check if old plugins exits
            if (pluginRepositories.Exists(repo => repo != null && repo.Url != null && oldRepos.Contains(repo.Url)))
            {
                // remove all old plugins
                pluginRepositories.RemoveAll(repo => repo != null && repo.Url != null && oldRepos.Contains(repo.Url));

                // Add repository only if it does not exit
                if (!pluginRepositories.Exists(repo => repo.Url == "https://manifest.intro-skipper.workers.dev/manifest.json"))
                {
                    // Add the new repository to the list
                    pluginRepositories.Add(new RepositoryInfo
                    {
                        Name = "intro skipper (automatically migrated by plugin)",
                        Url = "https://manifest.intro-skipper.workers.dev/manifest.json",
                        Enabled = true,
                    });
                }

                // Update the configuration with the new repository list
                config.PluginRepositories = [.. pluginRepositories];

                // Save the updated configuration
                serverConfiguration.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while migrating repo URL");
        }
    }

    /// <summary>
    /// Inject the skip button script into the web interface.
    /// </summary>
    /// <param name="webPath">Full path to index.html.</param>
    private void InjectSkipButton(string webPath)
    {
        string searchPattern = "dashboard-dashboard.*.chunk.js";
        string[] filePaths = Directory.GetFiles(webPath, searchPattern, SearchOption.TopDirectoryOnly);
        string pattern = @"buildVersion""\)\.innerText=""(?<buildVersion>\d+\.\d+\.\d+)"",.*?webVersion""\)\.innerText=""(?<webVersion>\d+\.\d+\.\d+)";
        string buildVersionString = "unknow";
        string webVersionString = "unknow";
        // Create a Regex object
        Regex regex = new Regex(pattern);

        // should be only one file but this safer
        foreach (var file in filePaths)
        {
            string dashBoardText = File.ReadAllText(file);
            // Perform the match
            Match match = regex.Match(dashBoardText);
            // search for buildVersion and webVersion
            if (match.Success)
            {
                buildVersionString = match.Groups["buildVersion"].Value;
                webVersionString = match.Groups["webVersion"].Value;
                _logger.LogInformation("Found jellyfin-web <{WebVersion}>", webVersionString);
                break;
            }
        }

        if (webVersionString != "unknow")
        {
            // append Revision
            webVersionString += ".0";
            if (Version.TryParse(webVersionString, out var webversion))
            {
                if (_applicationHost.ApplicationVersion != webversion)
                {
                    _logger.LogWarning("The jellyfin-web <{WebVersion}> NOT compatible with Jellyfin <{JellyfinVersion}>", webVersionString, _applicationHost.ApplicationVersion);
                }
                else
                {
                    _logger.LogInformation("The jellyfin-web <{WebVersion}> compatible with Jellyfin <{JellyfinVersion}>", webVersionString, _applicationHost.ApplicationVersion);
                }
            }
        }

        // search for controllers/playback/video/index.html
        searchPattern = "playback-video-index-html.*.chunk.js";
        filePaths = Directory.GetFiles(webPath, searchPattern, SearchOption.TopDirectoryOnly);

        // should be only one file but this safer
        foreach (var file in filePaths)
        {
            // search for class btnSkipIntro
            if (File.ReadAllText(file).Contains("btnSkipIntro", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Found a modified version of jellyfin-web with built-in skip button support.");
                return;
            }
        }

        // Inject the skip intro button code into the web interface.
        string indexPath = Path.Join(webPath, "index.html");

        // Parts of this code are based off of JellyScrub's script injection code.
        // https://github.com/nicknsy/jellyscrub/blob/main/Nick.Plugin.Jellyscrub/JellyscrubPlugin.cs#L38

        _logger.LogDebug("Reading index.html from {Path}", indexPath);
        string contents = File.ReadAllText(indexPath);

        // change URL with every relase to prevent the Browers from caching
        string scriptTag = "<script src=\"configurationpage?name=skip-intro-button.js&release=" + GetType().Assembly.GetName().Version + "\"></script>";

        // Only inject the script tag once
        if (contents.Contains(scriptTag, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("The skip button has already been injected.");
            return;
        }

        // remove old version if necessary
        pattern = @"<script src=""configurationpage\?name=skip-intro-button\.js.*<\/script>";
        contents = Regex.Replace(contents, pattern, string.Empty, RegexOptions.IgnoreCase);

        // Inject a link to the script at the end of the <head> section.
        // A regex is used here to ensure the replacement is only done once.
        Regex headEnd = new Regex(@"</head>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        contents = headEnd.Replace(contents, scriptTag + "</head>", 1);

        // Write the modified file contents
        File.WriteAllText(indexPath, contents);

        _logger.LogInformation("Skip button added successfully.");
    }
}
