// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Common.Configuration;

namespace IntroSkipper.Db;

/// <summary>
/// Single source of truth for the plugin's database file locations, shared by the
/// plugin constructor and the dependency-injection registrations so the two can
/// never diverge.
/// </summary>
internal static class IntroSkipperDatabasePaths
{
    /// <summary>
    /// Name of the plugin data directory below <see cref="IApplicationPaths.DataPath"/>.
    /// </summary>
    internal const string PluginDirectoryName = "introskipper";

    /// <summary>
    /// File name of the segment database.
    /// </summary>
    internal const string SegmentDatabaseFileName = "introskipper-v2.db";

    /// <summary>
    /// File name of the legacy segment database. Read once (read-only) by
    /// <see cref="LegacyDatabaseImporter"/> and never modified, so downgrading
    /// to a pre-v2 plugin keeps working.
    /// </summary>
    internal const string LegacySegmentDatabaseFileName = "introskipper.db";

    /// <summary>
    /// File name of the detection cache database.
    /// </summary>
    internal const string DetectionCacheDatabaseFileName = "introskipper-cache.db";

    /// <summary>
    /// Returns the plugin data directory, creating it when missing.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <returns>The plugin data directory.</returns>
    internal static string GetPluginDirectory(IApplicationPaths applicationPaths)
    {
        var directory = Path.Join(applicationPaths.DataPath, PluginDirectoryName);

        // Directory.CreateDirectory is a no-op when the directory already exists.
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Returns the full path of the segment database, creating the containing directory when missing.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <returns>The segment database path.</returns>
    internal static string GetSegmentDatabasePath(IApplicationPaths applicationPaths)
        => Path.Join(GetPluginDirectory(applicationPaths), SegmentDatabaseFileName);

    /// <summary>
    /// Returns the legacy segment database path that sits next to the given current
    /// segment database path.
    /// </summary>
    /// <param name="segmentDatabasePath">Path of the current segment database.</param>
    /// <returns>The legacy segment database path.</returns>
    internal static string GetLegacySegmentDatabasePath(string segmentDatabasePath)
        => Path.Join(Path.GetDirectoryName(segmentDatabasePath), LegacySegmentDatabaseFileName);

    /// <summary>
    /// Returns the full path of the detection cache database, creating the containing directory when missing.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <returns>The detection cache database path.</returns>
    internal static string GetDetectionCacheDatabasePath(IApplicationPaths applicationPaths)
        => Path.Join(GetPluginDirectory(applicationPaths), DetectionCacheDatabaseFileName);
}
