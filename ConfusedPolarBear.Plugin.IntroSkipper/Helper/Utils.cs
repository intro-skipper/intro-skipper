using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Helper
{
    /// <summary>
    /// Utility methods for the IntroSkipper plugin.
    /// </summary>
    public static partial class Utils
    {
        /// <summary>
        /// Gets or sets the logger.
        /// </summary>
        public static ILogger? Logger { get; set; }

        /// <summary>
        /// Gets the production year of the series.
        /// </summary>
        /// <param name="seriesId">The ID of the series.</param>
        /// <returns>The production year as a string.</returns>
        public static string GetProductionYear(Guid seriesId)
        {
            return seriesId == Guid.Empty
                ? "Unknown"
                : Plugin.Instance?.GetItem(seriesId)?.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "Unknown";
        }

        /// <summary>
        /// Gets the library name of the series.
        /// </summary>
        /// <param name="seriesId">The ID of the series.</param>
        /// <returns>The library name as a string.</returns>
        public static string GetLibraryName(Guid seriesId)
        {
            if (seriesId == Guid.Empty)
            {
                return "Unknown";
            }

            var collectionFolders = Plugin.Instance?.GetCollectionFolders(seriesId);
            return collectionFolders?.Count > 0
                ? string.Join(", ", collectionFolders.Select(folder => folder.Name))
                : "Unknown";
        }

        /// <summary>
        /// Inject the skip button script into the web interface.
        /// </summary>
        /// <param name="webPath">Full path to index.html.</param>
        /// <param name="version">Plugin Version.</param>
        public static void InjectSkipButton(string webPath, Version version)
        {
            // search for controllers/playback/video/index.html
            string searchPattern = "playback-video-index-html.*.chunk.js";
            string[] filePaths = Directory.GetFiles(webPath, searchPattern, SearchOption.TopDirectoryOnly);

            // should be only one file but this safer
            foreach (var file in filePaths)
            {
                // search for class btnSkipIntro
                if (File.ReadAllText(file).Contains("btnSkipIntro", StringComparison.OrdinalIgnoreCase))
                {
                    Logger?.LogInformation("jellyfin has build-in skip button");
                    return;
                }
            }

            // Inject the skip intro button code into the web interface.
            string indexPath = Path.Join(webPath, "index.html");

            // Parts of this code are based off of JellyScrub's script injection code.
            // https://github.com/nicknsy/jellyscrub/blob/main/Nick.Plugin.Jellyscrub/JellyscrubPlugin.cs#L38

            Logger?.LogDebug("Reading index.html from {Path}", indexPath);
            string contents = File.ReadAllText(indexPath);

            // change URL with every relase to prevent the Browers from caching
            string scriptTag = "<script src=\"configurationpage?name=skip-intro-button.js&release=" + version + "\"></script>";

            // Only inject the script tag once
            if (contents.Contains(scriptTag, StringComparison.OrdinalIgnoreCase))
            {
                Logger?.LogInformation("Skip button already added");
                return;
            }

            // remove old version if necessary
            string pattern = @"<script src=""configurationpage\?name=skip-intro-button\.js.*<\/script>";
            contents = Regex.Replace(contents, pattern, string.Empty, RegexOptions.IgnoreCase);

            // Inject a link to the script at the end of the <head> section.
            // A regex is used here to ensure the replacement is only done once.
            Regex headEnd = HeadRegex();
            contents = headEnd.Replace(contents, scriptTag + "</head>", 1);

            // Write the modified file contents
            File.WriteAllText(indexPath, contents);

            Logger?.LogInformation("Skip intro button successfully added");
        }

        [GeneratedRegex("</head>", RegexOptions.IgnoreCase)]
        private static partial Regex HeadRegex();
    }
}
