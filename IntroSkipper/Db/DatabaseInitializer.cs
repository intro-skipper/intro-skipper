// SPDX-FileCopyrightText: 2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Owns the lifecycle of both plugin databases: migrations and legacy schema repair
/// for <c>introskipper.db</c>, create-or-recover for <c>introskipper-cache.db</c>, and
/// the destructive rebuild flow.
///
/// <para>Each database has its own independent one-time initialization gate, awaited by the
/// matching gated <see cref="IDbContextFactory{TContext}"/> before any context is handed out,
/// so no query can observe an unmigrated database. The gates are isolated on purpose: the two
/// databases live in separate files precisely so cache corruption cannot affect segment data,
/// and a cache initialization failure must therefore never block segment-database access.
/// Initialization never throws — failures are logged and surface later as per-query errors,
/// matching the previous Plugin-constructor behavior.</para>
/// </summary>
public sealed partial class DatabaseInitializer
{
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly DbContextOptions<IntroSkipperDbContext> _segmentOptions;
    private readonly DbContextOptions<DetectionCacheDbContext> _cacheOptions;
    private readonly object _initLock = new();
    private Task? _segmentInitTask;
    private Task? _cacheInitTask;

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
    /// Ensures one-time initialization of both databases has completed, starting it if necessary.
    /// Safe to call concurrently from any thread; never throws initialization errors.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (abandons the wait, not the initialization).</param>
    /// <returns>A task that completes when initialization of both databases has finished.</returns>
    public Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
        => Task.WhenAll(GetOrStartSegmentInitTask(), GetOrStartCacheInitTask()).WaitAsync(cancellationToken);

    /// <summary>
    /// Ensures one-time initialization of the segment database has completed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (abandons the wait, not the initialization).</param>
    /// <returns>A task that completes when segment database initialization has finished.</returns>
    public Task EnsureSegmentDatabaseInitializedAsync(CancellationToken cancellationToken = default)
        => GetOrStartSegmentInitTask().WaitAsync(cancellationToken);

    /// <summary>
    /// Ensures one-time initialization of the detection cache database has completed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (abandons the wait, not the initialization).</param>
    /// <returns>A task that completes when cache database initialization has finished.</returns>
    public Task EnsureCacheDatabaseInitializedAsync(CancellationToken cancellationToken = default)
        => GetOrStartCacheInitTask().WaitAsync(cancellationToken);

    /// <summary>
    /// Synchronously ensures one-time initialization of the segment database has completed.
    /// Used only by the synchronous <c>CreateDbContext</c> factory path; async call sites must
    /// use <c>CreateDbContextAsync</c> so a slow first migration does not pin thread-pool threads.
    /// </summary>
    public void EnsureSegmentDatabaseInitialized() => GetOrStartSegmentInitTask().GetAwaiter().GetResult();

    /// <summary>
    /// Synchronously ensures one-time initialization of the detection cache database has completed.
    /// Used only by the synchronous <c>CreateDbContext</c> factory path (e.g. the synchronous
    /// <c>IDetectionCacheService</c> surface).
    /// </summary>
    public void EnsureCacheDatabaseInitialized() => GetOrStartCacheInitTask().GetAwaiter().GetResult();

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
        await EnsureSegmentDatabaseInitializedAsync(cancellationToken).ConfigureAwait(false);

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

    private Task GetOrStartSegmentInitTask()
    {
        lock (_initLock)
        {
            return _segmentInitTask ??= Task.Run(InitializeSegmentDatabaseCore);
        }
    }

    private Task GetOrStartCacheInitTask()
    {
        lock (_initLock)
        {
            return _cacheInitTask ??= Task.Run(InitializeCacheDatabaseCore);
        }
    }

    private void InitializeSegmentDatabaseCore()
    {
        // Failures are logged but never thrown so a broken database cannot fault the shared
        // init task (which would poison every later context creation) or abort host startup.
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
    }

    private void InitializeCacheDatabaseCore()
    {
        // Catch-all on purpose: any escaped exception type (UnauthorizedAccessException,
        // InvalidOperationException, ...) would otherwise fault the cache gate permanently.
        // The cache is reconstructable; per-query failures are handled by DetectionCacheService.
        try
        {
            EnsureDatabaseDirectoryExists(_cacheOptions);
            using var cacheDb = new DetectionCacheDbContext(_cacheOptions);
            cacheDb.EnsureSchema();
        }
        catch (Exception ex)
        {
            LogCacheDbInitializationError(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing database")]
    private static partial void LogDatabaseInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing detection cache database")]
    private static partial void LogCacheDbInitializationError(ILogger logger, Exception exception);
}
