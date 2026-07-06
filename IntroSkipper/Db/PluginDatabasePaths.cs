// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Common.Configuration;

namespace IntroSkipper.Db;

/// <summary>
/// Computes the on-disk locations of the plugin databases from the Jellyfin application paths.
/// Kept in one place so the DI registrations and the <see cref="Plugin"/> constructor can never diverge.
/// </summary>
public static class PluginDatabasePaths
{
    private const string PluginDirectoryName = "introskipper";
    private const string SegmentDbFileName = "introskipper.db";
    private const string CacheDbFileName = "introskipper-cache.db";

    /// <summary>
    /// Gets the plugin data directory that contains both database files.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <returns>Absolute path of the plugin data directory.</returns>
    public static string GetPluginDataDirectory(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        return Path.Join(applicationPaths.DataPath, PluginDirectoryName);
    }

    /// <summary>
    /// Gets the path of the segment/season-state database (<c>introskipper.db</c>).
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <returns>Absolute path of the segment database file.</returns>
    public static string GetSegmentDbPath(IApplicationPaths applicationPaths)
        => Path.Join(GetPluginDataDirectory(applicationPaths), SegmentDbFileName);

    /// <summary>
    /// Gets the path of the detection cache database (<c>introskipper-cache.db</c>).
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <returns>Absolute path of the detection cache database file.</returns>
    public static string GetCacheDbPath(IApplicationPaths applicationPaths)
        => Path.Join(GetPluginDataDirectory(applicationPaths), CacheDbFileName);
}
