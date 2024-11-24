using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Helper;

internal static class LegacyMigrations
{
    public static void MigrateAll(
        Plugin plugin,
        IServerConfigurationManager serverConfiguration,
        ILogger logger,
        IApplicationPaths applicationPaths,
        ILibraryManager libraryManager)
    {
        var pluginDirName = "introskipper";
        var introPath = Path.Join(applicationPaths.DataPath, pluginDirName, "intros.xml");
        var creditsPath = Path.Join(applicationPaths.DataPath, pluginDirName, "credits.xml");
        // Migrate XML files from XMLSchema to DataContract
        XmlSerializationHelper.MigrateXML(introPath);
        XmlSerializationHelper.MigrateXML(creditsPath);

        MigrateConfig(plugin, applicationPaths.PluginConfigurationsPath, logger);
        MigrateRepoUrl(plugin, serverConfiguration, logger);
        MigrateSettingsToJellyfin(plugin, logger, libraryManager);
        InjectSkipButton(plugin, applicationPaths.WebPath, logger);
        RestoreTimestamps(plugin.DbPath, introPath, creditsPath);
    }

    private static void MigrateConfig(Plugin plugin, string pluginConfigurationsPath, ILogger logger)
    {
        var oldConfigFile = Path.Join(pluginConfigurationsPath, "ConfusedPolarBear.Plugin.IntroSkipper.xml");

        if (File.Exists(oldConfigFile))
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(PluginConfiguration));
                using FileStream fileStream = new FileStream(oldConfigFile, FileMode.Open);
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, // Disable DTD processing
                    XmlResolver = null // Disable the XmlResolver
                };

                using var reader = XmlReader.Create(fileStream, settings);
                if (serializer.Deserialize(reader) is PluginConfiguration oldConfig)
                {
                    plugin.UpdateConfiguration(oldConfig);
                    fileStream.Close();
                    File.Delete(oldConfigFile);
                }
            }
            catch (Exception ex)
            {
                // Handle exceptions, such as file not found, deserialization errors, etc.
                logger.LogWarning("Failed to migrate from the ConfusedPolarBear Config {Exception}", ex);
            }
        }
    }

    private static void MigrateRepoUrl(Plugin plugin, IServerConfigurationManager serverConfiguration, ILogger logger)
    {
        try
        {
            List<string> oldRepos =
            [
                "https://raw.githubusercontent.com/intro-skipper/intro-skipper/master/manifest.json",
                "https://raw.githubusercontent.com/jumoog/intro-skipper/master/manifest.json",
                "https://manifest.intro-skipper.workers.dev/manifest.json"
            ];

            var config = serverConfiguration.Configuration;
            var pluginRepositories = config.PluginRepositories.ToList();

            if (pluginRepositories.Exists(repo => repo.Url != null && oldRepos.Contains(repo.Url)))
            {
                pluginRepositories.RemoveAll(repo => repo.Url != null && oldRepos.Contains(repo.Url));

                if (!pluginRepositories.Exists(repo => repo.Url == "https://manifest.intro-skipper.org/manifest.json") && plugin.Configuration.OverrideManifestUrl)
                {
                    pluginRepositories.Add(new RepositoryInfo
                    {
                        Name = "intro skipper (automatically migrated by plugin)",
                        Url = "https://manifest.intro-skipper.org/manifest.json",
                        Enabled = true,
                    });
                }

                config.PluginRepositories = [.. pluginRepositories];
                serverConfiguration.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred while migrating repo URL");
        }
    }

    private static void InjectSkipButton(Plugin plugin, string webPath, ILogger logger)
    {
        string pattern = @"<script src=""configurationpage\?name=skip-intro-button\.js.*<\/script>";
        string indexPath = Path.Join(webPath, "index.html");

        // Check if we can actually access the file
        bool canAccessFile = false;
        try
        {
            if (File.Exists(indexPath))
            {
                using var fs = File.Open(indexPath, FileMode.Open, FileAccess.ReadWrite);
                canAccessFile = true;
            }
        }
        catch (Exception)
        {
            // If skip button is disabled and we can't access the file, just return silently
            if (!plugin.Configuration.SkipButtonEnabled)
            {
                logger.LogDebug("Skip button disabled and no permission to access index.html. Assuming its a fresh install.");
                return;
            }

            WarningManager.SetFlag(PluginWarning.UnableToAddSkipButton);
            logger.LogError("Failed to add skip button to web interface. See https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting#skip-button-is-not-visible for the most common issues.");
            return;
        }

        if (!canAccessFile)
        {
            logger.LogDebug("Jellyfin running as nowebclient");
            return;
        }

        try
        {
            logger.LogInformation("Reading index.html from {Path}", indexPath);
            string contents = File.ReadAllText(indexPath);

            if (!plugin.Configuration.SkipButtonEnabled)
            {
                if (!Regex.IsMatch(contents, pattern, RegexOptions.IgnoreCase))
                {
                    logger.LogDebug("Skip button not found. Assuming its a fresh install.");
                    return;
                }

                logger.LogInformation("Skip button found. Removing the Skip button.");
                contents = Regex.Replace(contents, pattern, string.Empty, RegexOptions.IgnoreCase);
                File.WriteAllText(indexPath, contents);
                return;
            }

            string scriptTag = "<script src=\"configurationpage?name=skip-intro-button.js&release=" + plugin.GetType().Assembly.GetName().Version + "\"></script>";
            if (contents.Contains(scriptTag, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("The skip button has already been injected.");
                return;
            }

            contents = Regex.Replace(contents, pattern, string.Empty, RegexOptions.IgnoreCase);

            Regex headEnd = new Regex(@"</head>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            contents = headEnd.Replace(contents, scriptTag + "</head>", 1);

            File.WriteAllText(indexPath, contents);
            logger.LogInformation("Skip button added successfully.");
        }
        catch (UnauthorizedAccessException)
        {
            WarningManager.SetFlag(PluginWarning.UnableToAddSkipButton);
            logger.LogError("Failed to add skip button to web interface. See https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting#skip-button-is-not-visible for the most common issues.");
        }
        catch (IOException)
        {
            WarningManager.SetFlag(PluginWarning.UnableToAddSkipButton);
            logger.LogError("Failed to add skip button to web interface. See https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting#skip-button-is-not-visible for the most common issues.");
        }
    }

    private static void RestoreTimestamps(string dbPath, string introPath, string creditsPath)
    {
        using var db = new IntroSkipperDbContext(dbPath);
        // Import intros
        if (File.Exists(introPath))
        {
            var introList = XmlSerializationHelper.DeserializeFromXml<Segment>(introPath);
            foreach (var intro in introList)
            {
                db.DbSegment.Add(new DbSegment(intro, AnalysisMode.Introduction));
            }
        }

        // Import credits
        if (File.Exists(creditsPath))
        {
            var creditList = XmlSerializationHelper.DeserializeFromXml<Segment>(creditsPath);
            foreach (var credit in creditList)
            {
                db.DbSegment.Add(new DbSegment(credit, AnalysisMode.Credits));
            }
        }

        db.SaveChanges();

        File.Delete(introPath);
        File.Delete(creditsPath);
    }

    private static void MigrateSettingsToJellyfin(Plugin plugin, ILogger logger, ILibraryManager libraryManager)
    {
        try
        {
            if (!plugin.Configuration.SelectAllLibraries)
            {
                logger.LogInformation("Migration of your old library settings to Jellyfin");
                List<string> selectedLibraries = [.. plugin.Configuration.SelectedLibraries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                foreach (var folder in libraryManager.GetVirtualFolders())
                {
                    if (!selectedLibraries.Contains(folder.Name) && folder.CollectionType is CollectionTypeOptions.tvshows or CollectionTypeOptions.movies or CollectionTypeOptions.mixed)
                    {
                        // only add if not already disabled
                        if (!folder.LibraryOptions.DisabledMediaSegmentProviders.Contains(plugin.Name))
                        {
                            // ppend in case there other disabled media segment providers
                            folder.LibraryOptions.DisabledMediaSegmentProviders = [.. folder.LibraryOptions.DisabledMediaSegmentProviders, plugin.Name];
                            logger.LogInformation("Disable Media Segment Provider <{Name}> for Library <{Name}>", plugin.Name, folder.Name);
                        }
                    }
                }

                // reset to default
                plugin.Configuration.SelectAllLibraries = true;
                plugin.Configuration.SelectedLibraries = string.Empty;
                plugin.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("The migration of your old library settings to Jellyfin has failed: {Exception}", ex);
        }
        finally
        {
            // reset to default
            plugin.Configuration.SelectAllLibraries = true;
            plugin.Configuration.SelectedLibraries = string.Empty;
            plugin.SaveConfiguration();
        }
    }
}
