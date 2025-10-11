using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;
using static System.Net.WebRequestMethods;

namespace IntroSkipper.Helper;

internal static class LegacyMigrations
{
    public static void MigrateAll(
        Plugin plugin,
        IServerConfigurationManager serverConfiguration,
        ILogger logger,
        ILibraryManager libraryManager)
    {
        MigrateSettingsToJellyfin(plugin, logger, libraryManager);
        MigrateRepoUrl(plugin, serverConfiguration, logger);
    }

    private static void MigrateRepoUrl(Plugin plugin, IServerConfigurationManager serverConfiguration, ILogger logger)
    {
        try
        {
            List<string> oldRepos =
            [
                "https://raw.githubusercontent.com/intro-skipper/intro-skipper/master/manifest.json",
                "https://raw.githubusercontent.com/jumoog/intro-skipper/master/manifest.json",
                "https://manifest.intro-skipper.workers.dev/manifest.json",
                "https://manifest.intro-skipper.org/manifest.json"
            ];

            var config = serverConfiguration.Configuration;
            var pluginRepositories = config.PluginRepositories.ToList();

            if (pluginRepositories.Exists(repo => repo.Url != null && oldRepos.Contains(repo.Url)))
            {
                pluginRepositories.RemoveAll(repo => repo.Url != null && oldRepos.Contains(repo.Url));

                if (!pluginRepositories.Exists(repo => repo.Url == "https://intro-skipper.org/manifest.json") && plugin.Configuration.OverrideManifestUrl)
                {
                    pluginRepositories.Add(new RepositoryInfo
                    {
                        Name = "intro skipper Plugin Repository (automatically migrated by plugin)",
                        Url = "https://intro-skipper.org/manifest.json",
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
                            // append in case there other disabled media segment providers
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

            if (!plugin.Configuration.AnalyzeMovies)
            {
                logger.LogInformation("Migration of your old Movie settings to Jellyfin");
                foreach (var folder in libraryManager.GetVirtualFolders())
                {
                    if (folder.CollectionType is CollectionTypeOptions.movies or CollectionTypeOptions.mixed)
                    {
                        // only add if not already disabled
                        if (!folder.LibraryOptions.DisabledMediaSegmentProviders.Contains(plugin.Name))
                        {
                            // append in case there other disabled media segment providers
                            folder.LibraryOptions.DisabledMediaSegmentProviders = [.. folder.LibraryOptions.DisabledMediaSegmentProviders, plugin.Name];
                            logger.LogInformation("Disable Media Segment Provider <{Name}> for Library <{Name}>", plugin.Name, folder.Name);
                        }
                    }
                }

                // reset to default
                plugin.Configuration.AnalyzeMovies = true;
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
