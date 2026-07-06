// SPDX-FileCopyrightText: 2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Central definition of the plugin's database file locations and SQLite options.
/// Shared by the DI registrations in <c>PluginServiceRegistrator</c>, the
/// <see cref="DatabaseInitializer"/>, and <c>Plugin</c> so every component agrees
/// on paths and connection configuration.
/// </summary>
public static class IntroSkipperDatabase
{
    private const string PluginDirectoryName = "introskipper";
    private const string SegmentDatabaseFileName = "introskipper.db";
    private const string CacheDatabaseFileName = "introskipper-cache.db";

    private static readonly SqlitePragmaInterceptor _pragmaInterceptor = new();

    /// <summary>
    /// Gets the plugin data directory (containing both database files).
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <returns>The plugin data directory.</returns>
    public static string GetPluginDirectory(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        return Path.Join(applicationPaths.DataPath, PluginDirectoryName);
    }

    /// <summary>
    /// Gets the path of the segment/season-state database (<c>introskipper.db</c>).
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <returns>The segment database path.</returns>
    public static string GetSegmentDatabasePath(IApplicationPaths applicationPaths)
        => Path.Join(GetPluginDirectory(applicationPaths), SegmentDatabaseFileName);

    /// <summary>
    /// Gets the path of the detection cache database (<c>introskipper-cache.db</c>).
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <returns>The cache database path.</returns>
    public static string GetCacheDatabasePath(IApplicationPaths applicationPaths)
        => Path.Join(GetPluginDirectory(applicationPaths), CacheDatabaseFileName);

    /// <summary>
    /// Applies the plugin's standard SQLite configuration (connection string and
    /// PRAGMA interceptor) to a context options builder. This is the single source
    /// of truth for runtime connection configuration; the string-path context
    /// constructors apply the equivalent configuration in <c>OnConfiguring</c>.
    /// </summary>
    /// <param name="optionsBuilder">The options builder to configure.</param>
    /// <param name="dbPath">Path of the SQLite database file.</param>
    internal static void ConfigureSqlite(DbContextOptionsBuilder optionsBuilder, string dbPath)
    {
        optionsBuilder
            .UseSqlite($"Data Source={dbPath}")
            .AddInterceptors(_pragmaInterceptor);
    }

    /// <summary>
    /// Extracts the database file path from configured context options, or
    /// <see langword="null"/> when no file-backed data source is configured.
    /// </summary>
    /// <param name="options">Configured context options.</param>
    /// <returns>The database file path, or <see langword="null"/>.</returns>
    internal static string? GetDatabasePath(DbContextOptions options)
    {
        var extension = options.Extensions
            .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
            .FirstOrDefault();
        var connectionString = extension?.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        return builder.DataSource is not (null or "" or ":memory:") ? builder.DataSource : null;
    }
}
