// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Default <see cref="IDatabaseInitializer"/> implementation. Thread safety is provided by
/// <see cref="Lazy{T}"/> gates with <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>,
/// so each database is initialized exactly once regardless of how many stores race on first use.
/// </summary>
internal sealed partial class DatabaseInitializer : IDatabaseInitializer
{
    private readonly IDbContextFactory<IntroSkipperDbContext> _segmentContextFactory;
    private readonly IDbContextFactory<DetectionCacheDbContext> _cacheContextFactory;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly Lazy<Task> _segmentDbInitialization;
    private readonly Lazy<bool> _cacheDbInitialization;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseInitializer"/> class.
    /// </summary>
    /// <param name="segmentContextFactory">Factory for segment database contexts.</param>
    /// <param name="cacheContextFactory">Factory for detection cache database contexts.</param>
    /// <param name="logger">Logger.</param>
    public DatabaseInitializer(
        IDbContextFactory<IntroSkipperDbContext> segmentContextFactory,
        IDbContextFactory<DetectionCacheDbContext> cacheContextFactory,
        ILogger<DatabaseInitializer> logger)
    {
        _segmentContextFactory = segmentContextFactory;
        _cacheContextFactory = cacheContextFactory;
        _logger = logger;
        _segmentDbInitialization = new Lazy<Task>(InitializeSegmentDbAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        _cacheDbInitialization = new Lazy<bool>(InitializeCacheDb, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc/>
    public Task EnsureSegmentDbReadyAsync(CancellationToken cancellationToken = default)
        => _segmentDbInitialization.Value.WaitAsync(cancellationToken);

    /// <inheritdoc/>
    public void EnsureCacheDbReady() => _ = _cacheDbInitialization.Value;

    /// <inheritdoc/>
    public async Task RebuildSegmentDatabaseAsync(bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default)
    {
        // Never rebuild concurrently with (or before) first-time initialization.
        await EnsureSegmentDbReadyAsync(cancellationToken).ConfigureAwait(false);

        using var db = _segmentContextFactory.CreateDbContext();
        await db.RebuildDatabaseAsync(_segmentContextFactory.CreateDbContext, forceCleanOnBackupFailure, cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeSegmentDbAsync()
    {
        try
        {
            using var db = _segmentContextFactory.CreateDbContext();

            // Legacy databases may be missing migration history or columns that EF migrations expect.
            // Normalize those schemas first so recovery does not log a false initialization failure.
            db.EnsureLegacySchemaCompatibility();
            await db.ApplyMigrationsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Parity with the historical Plugin constructor: log and continue. Queries against a
            // genuinely unusable database will fail with actionable errors at the call sites.
            LogSegmentDbInitializationError(_logger, ex);
        }
    }

    private bool InitializeCacheDb()
    {
        try
        {
            using var cacheDb = _cacheContextFactory.CreateDbContext();
            cacheDb.EnsureSchema();
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            LogCacheDbInitializationError(_logger, ex);
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing database")]
    private static partial void LogSegmentDbInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing detection cache database")]
    private static partial void LogCacheDbInitializationError(ILogger logger, Exception exception);
}
