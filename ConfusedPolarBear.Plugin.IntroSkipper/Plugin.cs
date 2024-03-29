using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ConfusedPolarBear.Plugin.IntroSkipper.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Intro skipper plugin. Uses audio analysis to find common sequences of audio shared between episodes.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly object _serializationLock = new();
    private readonly object _introsLock = new();
    private IXmlSerializer _xmlSerializer;
    private ILibraryManager _libraryManager;
    private IItemRepository _itemRepository;
    private ILogger<Plugin> _logger;
    private string _introPath;
    private string _creditsPath;
    private string _oldintroPath;
    private string _oldcreditsPath;
    private string _oldFingerprintCachePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="serverConfiguration">Server configuration manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="itemRepository">Item repository.</param>
    /// <param name="logger">Logger.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServerConfigurationManager serverConfiguration,
        ILibraryManager libraryManager,
        IItemRepository itemRepository,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        _xmlSerializer = xmlSerializer;
        _libraryManager = libraryManager;
        _itemRepository = itemRepository;
        _logger = logger;

        FFmpegPath = serverConfiguration.GetEncodingOptions().EncoderAppPathDisplay;

        var introsDirectory = Path.Join(applicationPaths.CachePath, "introskipper");
        FingerprintCachePath = Path.Join(introsDirectory, "chromaprints");
        _introPath = Path.Join(applicationPaths.CachePath, "introskipper", "intros.xml");
        _creditsPath = Path.Join(applicationPaths.CachePath, "introskipper", "credits.xml");

        var oldintrosDirectory = Path.Join(applicationPaths.PluginConfigurationsPath, "intros");
        _oldFingerprintCachePath = Path.Join(oldintrosDirectory, "cache");
        _oldintroPath = Path.Join(applicationPaths.PluginConfigurationsPath, "intros", "intros.xml");
        _oldcreditsPath = Path.Join(applicationPaths.PluginConfigurationsPath, "intros", "credits.xml");

        // Create the base & cache directories (if needed).
        if (!Directory.Exists(FingerprintCachePath))
        {
            Directory.CreateDirectory(FingerprintCachePath);

            // Check if the old cache directory exists
            if (Directory.Exists(_oldFingerprintCachePath))
            {
                // Move the contents from old directory to new directory
                File.Move(_oldintroPath, _introPath);
                File.Move(_oldcreditsPath, _creditsPath);
                string[] files = Directory.GetFiles(_oldFingerprintCachePath);
                foreach (string file in files)
                {
                    string fileName = Path.GetFileName(file);
                    string destFile = Path.Combine(FingerprintCachePath, fileName);
                    File.Move(file, destFile);
                }

                // Optionally, you may delete the old directory after moving its contents
                Directory.Delete(oldintrosDirectory, true);
            }
        }

        ConfigurationChanged += OnConfigurationChanged;

        // TODO: remove when https://github.com/jellyfin/jellyfin-meta/discussions/30 is complete
        try
        {
            RestoreTimestamps();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Unable to load introduction timestamps: {Exception}", ex);
        }

        // Inject the skip intro button code into the web interface.
        var indexPath = Path.Join(applicationPaths.WebPath, "index.html");
        try
        {
            InjectSkipButton(indexPath);
        }
        catch (Exception ex)
        {
            WarningManager.SetFlag(PluginWarning.UnableToAddSkipButton);

            if (ex is UnauthorizedAccessException)
            {
                var suggestion = OperatingSystem.IsLinux() ?
                    "running `sudo chown jellyfin PATH` (if this is a native installation)" :
                    "changing the permissions of PATH";

                suggestion = suggestion.Replace("PATH", indexPath, StringComparison.Ordinal);

                _logger.LogError(
                    "Failed to add skip button to web interface. Try {Suggestion} and restarting the server. Error: {Error}",
                    suggestion,
                    ex);
            }
            else
            {
                _logger.LogError("Unknown error encountered while adding skip button: {Error}", ex);
            }
        }

        FFmpegWrapper.CheckFFmpegVersion();
    }

    /// <summary>
    /// Gets or sets a value indicating whether analysis is running.
    /// </summary>
    public bool AnalyzerTaskIsRunning { get; set; } = false;

    /// <summary>
    /// Fired after configuration has been saved so the auto skip timer can be stopped or started.
    /// </summary>
    public event EventHandler? AutoSkipChanged;

    /// <summary>
    /// Gets the results of fingerprinting all episodes.
    /// </summary>
    public Dictionary<Guid, Intro> Intros { get; } = new();

    /// <summary>
    /// Gets all discovered ending credits.
    /// </summary>
    public Dictionary<Guid, Intro> Credits { get; } = new();

    /// <summary>
    /// Gets the most recent media item queue.
    /// </summary>
    public Dictionary<Guid, List<QueuedEpisode>> QueuedMediaItems { get; } = new();

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
    public void SaveTimestamps()
    {
        lock (_serializationLock)
        {
            var introList = new List<Intro>();

            // Serialize intros
            foreach (var intro in Plugin.Instance!.Intros)
            {
                introList.Add(intro.Value);
            }

            _xmlSerializer.SerializeToFile(introList, _introPath);

            // Serialize credits
            introList.Clear();

            foreach (var intro in Plugin.Instance!.Credits)
            {
                introList.Add(intro.Value);
            }

            _xmlSerializer.SerializeToFile(introList, _creditsPath);
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
            var introList = (List<Intro>)_xmlSerializer.DeserializeFromFile(
                typeof(List<Intro>),
                _introPath);

            foreach (var intro in introList)
            {
                Plugin.Instance!.Intros[intro.EpisodeId] = intro;
            }
        }

        if (File.Exists(_creditsPath))
        {
            var creditList = (List<Intro>)_xmlSerializer.DeserializeFromFile(
                typeof(List<Intro>),
                _creditsPath);

            foreach (var credit in creditList)
            {
                Plugin.Instance!.Credits[credit.EpisodeId] = credit;
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = this.Name,
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
        };
    }

    /// <summary>
    /// Gets the commit used to build the plugin.
    /// </summary>
    /// <returns>Commit.</returns>
    public string GetCommit()
    {
        var commit = string.Empty;

        var path = GetType().Namespace + ".Configuration.version.txt";
        using var stream = GetType().Assembly.GetManifestResourceStream(path);
        if (stream is null)
        {
            _logger.LogWarning("Unable to read embedded version information");
            return commit;
        }

        using var reader = new StreamReader(stream);
        commit = reader.ReadToEnd().TrimEnd();

        if (commit == "unknown")
        {
            _logger.LogTrace("Embedded version information was not valid, ignoring");
            return string.Empty;
        }

        _logger.LogInformation("Unstable plugin version built from commit {Commit}", commit);
        return commit;
    }

    internal BaseItem GetItem(Guid id)
    {
        return _libraryManager.GetItemById(id);
    }

    /// <summary>
    /// Gets the full path for an item.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <returns>Full path to item.</returns>
    internal string GetItemPath(Guid id)
    {
        return GetItem(id).Path;
    }

    /// <summary>
    /// Gets all chapters for this item.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <returns>List of chapters.</returns>
    internal List<ChapterInfo> GetChapters(Guid id)
    {
        return _itemRepository.GetChapters(GetItem(id));
    }

    internal void UpdateTimestamps(Dictionary<Guid, Intro> newTimestamps, AnalysisMode mode)
    {
        lock (_introsLock)
        {
            foreach (var intro in newTimestamps)
            {
                if (mode == AnalysisMode.Introduction)
                {
                    Plugin.Instance!.Intros[intro.Key] = intro.Value;
                }
                else if (mode == AnalysisMode.Credits)
                {
                    Plugin.Instance!.Credits[intro.Key] = intro.Value;
                }
            }

            Plugin.Instance!.SaveTimestamps();
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        AutoSkipChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Inject the skip button script into the web interface.
    /// </summary>
    /// <param name="indexPath">Full path to index.html.</param>
    private void InjectSkipButton(string indexPath)
    {
        // Parts of this code are based off of JellyScrub's script injection code.
        // https://github.com/nicknsy/jellyscrub/blob/main/Nick.Plugin.Jellyscrub/JellyscrubPlugin.cs#L38

        _logger.LogDebug("Reading index.html from {Path}", indexPath);
        var contents = File.ReadAllText(indexPath);
        _logger.LogDebug("Successfully read index.html");

        var scriptTag = "<script src=\"configurationpage?name=skip-intro-button.js\"></script>";

        // Only inject the script tag once
        if (contents.Contains(scriptTag, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Skip button already added");
            return;
        }

        // Inject a link to the script at the end of the <head> section.
        // A regex is used here to ensure the replacement is only done once.
        _logger.LogDebug("Injecting script tag");
        var headEnd = new Regex("</head>", RegexOptions.IgnoreCase);
        contents = headEnd.Replace(contents, scriptTag + "</head>", 1);

        // Write the modified file contents
        _logger.LogDebug("Saving modified file");
        File.WriteAllText(indexPath, contents);

        _logger.LogInformation("Skip intro button successfully added");
    }
}
