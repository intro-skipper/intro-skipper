// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Default implementation of <see cref="IIntroSkipperDatabase"/>.
/// The implementation is split across partial class files by concern:
/// <list type="bullet">
/// <item><description><c>IntroSkipperDatabase.cs</c> — lifecycle (initialization gate, migrations, rebuild).</description></item>
/// <item><description><c>IntroSkipperDatabase.Segments.cs</c> — <see cref="DbSegment"/> reads and writes.</description></item>
/// <item><description><c>IntroSkipperDatabase.SeasonStates.cs</c> — <see cref="DbSeasonState"/> reads and writes.</description></item>
/// <item><description><c>IntroSkipperDatabase.Maintenance.cs</c> — bulk cleanup operations spanning both tables.</description></item>
/// </list>
/// The facade is stateless apart from the retryable initialization gate: every operation
/// creates a fresh <see cref="IntroSkipperDbContext"/> from the injected factory, exactly
/// as the previous <c>Plugin</c>-hosted methods did.
/// </summary>
public sealed partial class IntroSkipperDatabase : IIntroSkipperDatabase
{
    private const double SegmentComparisonEpsilon = 0.001;

    private readonly IDbContextFactory<IntroSkipperDbContext> _contextFactory;
    private readonly ILogger _logger;
    private readonly object _initializationLock = new();
    private Lazy<Task> _initialization;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSkipperDatabase"/> class.
    /// </summary>
    /// <param name="contextFactory">Factory used to create database contexts.</param>
    /// <param name="logger">Logger.</param>
    public IntroSkipperDatabase(IDbContextFactory<IntroSkipperDbContext> contextFactory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _contextFactory = contextFactory;
        _logger = logger;
        // Task.Run is load-bearing, not redundant: InitializeCoreAsync begins with fully
        // synchronous work (EnsureLegacySchemaCompatibility can rebuild whole tables on
        // large legacy databases), so invoking the factory inline would make every
        // concurrent first-touch caller — including purely async ones on the playback
        // hot path — block its thread on the Lazy monitor until the factory's first
        // incomplete await. Dispatching to the thread pool makes the factory return a
        // Task immediately, so waiters genuinely await instead of blocking.
        _initialization = CreateInitialization();
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        var initialization = GetInitialization();

        try
        {
            await initialization.Value.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (TryResetInitialization(initialization))
            {
                LogDatabaseInitializationError(_logger, ex);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public async Task RebuildDatabaseAsync(bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        await db.RebuildDatabaseAsync(_contextFactory.CreateDbContext, forceCleanOnBackupFailure, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits the retryable initialization gate. Every public data operation calls this
    /// first, which guarantees that no query can observe the database before legacy
    /// schema repair and EF migrations have completed — regardless of whether the
    /// eager initializer (hosted service) has already run.
    /// </summary>
    /// <returns>The shared initialization task.</returns>
    private Task EnsureInitializedAsync() => InitializeAsync();

    private async Task InitializeCoreAsync()
    {
        using var db = _contextFactory.CreateDbContext();

        // Legacy databases may be missing migration history or columns that EF migrations
        // expect. Normalize those schemas first so recovery does not log a false
        // initialization failure.
        db.EnsureLegacySchemaCompatibility();
        await db.ApplyMigrationsAsync().ConfigureAwait(false);

        // WAL is a persistent database property, but EF only sets it when *it*
        // creates the database file. Enforce it idempotently so databases
        // vacuumed or recreated by external tooling are covered as well.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;").ConfigureAwait(false);
    }

    private Lazy<Task> CreateInitialization()
        => new(() => Task.Run(InitializeCoreAsync), LazyThreadSafetyMode.ExecutionAndPublication);

    private Lazy<Task> GetInitialization()
    {
        lock (_initializationLock)
        {
            return _initialization;
        }
    }

    private bool TryResetInitialization(Lazy<Task> failedInitialization)
    {
        lock (_initializationLock)
        {
            if (!ReferenceEquals(_initialization, failedInitialization))
            {
                return false;
            }

            _initialization = CreateInitialization();
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database initialization failed; the next database operation will retry")]
    private static partial void LogDatabaseInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping credits for episode {EpisodeId}: detected segment overlaps with introduction")]
    private static partial void LogCreditsOverlapWithIntro(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update timestamp for episode {EpisodeId}")]
    private static partial void LogFailedToUpdateTimestamp(ILogger logger, Exception ex, Guid episodeId);
}
