// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Default implementation of <see cref="IIntroSkipperDatabase"/>.
/// The implementation is split across partial class files by concern:
/// <list type="bullet">
/// <item><description><c>IntroSkipperDatabase.cs</c> — lifecycle (initialization gate, migrations, legacy import, rebuild).</description></item>
/// <item><description><c>IntroSkipperDatabase.Segments.cs</c> — <see cref="DbSegment"/> reads and writes.</description></item>
/// <item><description><c>IntroSkipperDatabase.SeasonStates.cs</c> — <see cref="DbSeasonState"/> reads and writes.</description></item>
/// <item><description><c>IntroSkipperDatabase.Maintenance.cs</c> — bulk cleanup operations spanning both tables.</description></item>
/// </list>
/// The facade is stateless apart from the retryable initialization gate: every operation
/// creates a fresh <see cref="IntroSkipperDbContext"/> from the injected factory.
/// </summary>
public sealed partial class IntroSkipperDatabase : IIntroSkipperDatabase
{
    private readonly IDbContextFactory<IntroSkipperDbContext> _contextFactory;
    private readonly ILogger _logger;
    private readonly RetryableInitializationGate<Task> _initialization;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSkipperDatabase"/> class.
    /// </summary>
    /// <param name="contextFactory">Factory used to create database contexts.</param>
    /// <param name="logger">Logger.</param>
    public IntroSkipperDatabase(IDbContextFactory<IntroSkipperDbContext> contextFactory, ILogger<IntroSkipperDatabase> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _contextFactory = contextFactory;
        _logger = logger;
        // Task.Run is load-bearing, not redundant: InitializeCoreAsync may perform the
        // one-time legacy import (a long synchronous read loop over the old database),
        // so invoking the factory inline would make every concurrent first-touch caller
        // — including purely async ones on the playback hot path — block its thread on
        // the Lazy monitor until the factory's first incomplete await. Dispatching to
        // the thread pool makes the factory return a Task immediately, so waiters
        // genuinely await instead of blocking.
        _initialization = new RetryableInitializationGate<Task>(() => Task.Run(InitializeCoreAsync));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every public data operation awaits this first, which guarantees that no query can
    /// observe the database before EF migrations and the one-time legacy import have
    /// completed regardless of whether the eager initializer (hosted service) has already run.
    /// </remarks>
    public Task InitializeAsync()
        => _initialization.AwaitValueAsync(ex => LogDatabaseInitializationError(_logger, ex));

    /// <inheritdoc/>
    public async Task RebuildDatabaseAsync(bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        await db.RebuildDatabaseAsync(_contextFactory.CreateDbContext, forceCleanOnBackupFailure, cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync()
    {
        using var db = _contextFactory.CreateDbContext();
        await db.ApplyMigrationsAsync().ConfigureAwait(false);
        await SqlitePragmas.EnforceWalAsync(db.Database).ConfigureAwait(false);
        await ImportLegacyDatabaseAsync(db).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the one-time legacy import. The import and its marker commit in a single
    /// transaction, so a crash mid-import leaves no marker and the next initialization
    /// retries; an import failure is logged and swallowed so initialization still
    /// succeeds with an empty (but healthy) database — the legacy file stays intact for
    /// manual recovery and the import is retried at the next process start.
    /// </summary>
    private async Task ImportLegacyDatabaseAsync(IntroSkipperDbContext db)
    {
        if (await db.ImportHistory.AnyAsync().ConfigureAwait(false))
        {
            return;
        }

        var databasePath = db.GetDatabaseFilePath();
        var legacyPath = string.IsNullOrEmpty(databasePath)
            ? null
            : IntroSkipperDatabasePaths.GetLegacySegmentDatabasePath(databasePath);
        var sourceFileFound = legacyPath is not null && File.Exists(legacyPath);

        try
        {
            var transaction = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var result = sourceFileFound
                    ? await LegacyDatabaseImporter.ImportAsync(db, legacyPath!, _logger).ConfigureAwait(false)
                    : new LegacyImportResult(0, 0, 0, "no legacy database");

                db.ImportHistory.Add(new DbImportRecord
                {
                    ImportedAt = DateTime.UtcNow,
                    SourceFileFound = sourceFileFound,
                    SegmentsImported = result.SegmentsImported,
                    SegmentsSkipped = result.SegmentsSkipped,
                    SeasonStatesImported = result.SeasonStatesImported,
                    Notes = result.Notes
                });
                await db.SaveChangesAsync().ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);

                if (sourceFileFound)
                {
                    LogLegacyImportCompleted(_logger, result.SegmentsImported, result.SegmentsSkipped, result.SeasonStatesImported);
                }
            }
        }
        catch (Exception ex)
        {
            // The transaction rolled back on dispose: no marker, no partial data visible.
            LogLegacyImportFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database initialization failed; the next database operation will retry")]
    private static partial void LogDatabaseInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Legacy database import completed: {Segments} segments ({Skipped} skipped), {SeasonStates} season states")]
    private static partial void LogLegacyImportCompleted(ILogger logger, int segments, int skipped, int seasonStates);

    [LoggerMessage(Level = LogLevel.Error, Message = "Legacy database import failed; continuing with an empty database and retrying at the next server start")]
    private static partial void LogLegacyImportFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping credits for episode {EpisodeId}: detected segment overlaps with introduction")]
    private static partial void LogCreditsOverlapWithIntro(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping automatic {Mode} segment for item {ItemId}: overlaps a segment the user deleted")]
    private static partial void LogAutoSegmentSuppressedByTombstone(ILogger logger, Data.AnalysisMode mode, Guid itemId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping automatic {Mode} segment for item {ItemId}: overlaps a user-provided segment")]
    private static partial void LogAutoSegmentSkippedForUserOverlap(ILogger logger, Data.AnalysisMode mode, Guid itemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update segments for item {ItemId}")]
    private static partial void LogFailedToUpdateSegments(ILogger logger, Exception ex, Guid itemId);
}
