// SPDX-FileCopyrightText: 2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Owns the lifecycle of both plugin databases: migrations and legacy schema repair
/// for <c>introskipper.db</c>, create-or-recover for <c>introskipper-cache.db</c>, and
/// the destructive rebuild flow. Initialization runs exactly once per process and is
/// awaited by the gated <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>
/// implementations before any context is handed out, so no query can observe an
/// unmigrated database.
/// </summary>
public sealed partial class DatabaseInitializer
{
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly DbContextOptions<IntroSkipperDbContext> _segmentOptions;
    private readonly DbContextOptions<DetectionCacheDbContext> _cacheOptions;
    private readonly object _initLock = new();
    private Task? _initTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseInitializer"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="segmentOptions">Options for the segment database context.</param>
    /// <param name="cacheOptions">Options for the detection cache database context.</param>
    public DatabaseInitializer(
        ILogger<DatabaseInitializer> logger,
        DbContextOptions<IntroSkipperDbContext> segmentOptions,
        DbContextOptions<DetectionCacheDbContext> cacheOptions)
    {
        _logger = logger;
        _segmentOptions = segmentOptions;
        _cacheOptions = cacheOptions;
    }

    /// <summary>
    /// Ensures one-time database initialization has completed, starting it if necessary.
    /// Safe to call concurrently from any thread; only the first caller triggers the work.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (abandons the wait, not the initialization).</param>
    /// <returns>A task that completes when initialization has finished.</returns>
    public Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
        => GetOrStartInitTask().WaitAsync(cancellationToken);

    /// <summary>
    /// Synchronously ensures one-time database initialization has completed.
    /// Used by the synchronous <c>CreateDbContext</c> factory path.
    /// </summary>
    public void EnsureInitialized() => GetOrStartInitTask().GetAwaiter().GetResult();

    /// <summary>
    /// Rebuilds the segment database, salvaging valid rows where possible.
    /// Runs to completion regardless of request cancellation because the rebuild is destructive.
    /// </summary>
    /// <param name="forceCleanOnBackupFailure">
    /// When <see langword="true"/>, rebuilds an empty database if the backup read fails;
    /// otherwise the rebuild aborts to avoid data loss.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task RebuildDatabaseAsync(bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default)
    {
        // Never rebuild concurrently with (or before) first-time initialization.
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        using var db = new IntroSkipperDbContext(_segmentOptions);
        await db.RebuildDatabaseAsync(
            () => new IntroSkipperDbContext(_segmentOptions),
            forceCleanOnBackupFailure,
            cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureDatabaseDirectoryExists(DbContextOptions options)
    {
        var dbPath = IntroSkipperDatabase.GetDatabasePath(options);
        if (!string.IsNullOrEmpty(dbPath) && Path.GetDirectoryName(dbPath) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }
    }

    private Task GetOrStartInitTask()
    {
        lock (_initLock)
        {
            return _initTask ??= Task.Run(InitializeCore);
        }
    }

    private void InitializeCore()
    {
        // Initialize the segment database. Failures are logged but never thrown so a broken
        // database does not take the whole plugin down — matches the previous Plugin-ctor behavior.
        try
        {
            EnsureDatabaseDirectoryExists(_segmentOptions);
            using var db = new IntroSkipperDbContext(_segmentOptions);

            // Legacy databases may be missing migration history or columns that EF migrations expect.
            // Normalize those schemas first so recovery does not log a false initialization failure.
            db.EnsureLegacySchemaCompatibility();
            db.ApplyMigrations();
        }
        catch (Exception ex)
        {
            LogDatabaseInitializationError(_logger, ex);
        }

        // Initialize the detection cache database (delete-and-recreate on corruption).
        try
        {
            EnsureDatabaseDirectoryExists(_cacheOptions);
            using var cacheDb = new DetectionCacheDbContext(_cacheOptions);
            cacheDb.EnsureSchema();
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            LogCacheDbInitializationError(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing database")]
    private static partial void LogDatabaseInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing detection cache database")]
    private static partial void LogCacheDbInitializationError(ILogger logger, Exception exception);
}
