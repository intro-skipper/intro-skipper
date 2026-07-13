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
/// The facade is stateless apart from the one-shot initialization gate: every operation
/// creates a fresh <see cref="IntroSkipperDbContext"/> from the injected factory, exactly
/// as the previous <c>Plugin</c>-hosted methods did.
/// </summary>
public sealed partial class IntroSkipperDatabase : IIntroSkipperDatabase
{
    private const double SegmentComparisonEpsilon = 0.001;

    private readonly IDbContextFactory<IntroSkipperDbContext> _contextFactory;
    private readonly ILogger _logger;
    private readonly Lazy<Task> _initialization;

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
        _initialization = new Lazy<Task>(() => Task.Run(InitializeCoreAsync), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc/>
    public Task InitializeAsync() => _initialization.Value;

    /// <inheritdoc/>
    public async Task RebuildDatabaseAsync(bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        await db.RebuildDatabaseAsync(_contextFactory.CreateDbContext, forceCleanOnBackupFailure, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits the one-shot initialization gate. Every public data operation calls this
    /// first, which guarantees that no query can observe the database before legacy
    /// schema repair and EF migrations have completed — regardless of whether the
    /// eager initializer (hosted service) has already run.
    /// </summary>
    /// <returns>The shared initialization task.</returns>
    private Task EnsureInitializedAsync() => _initialization.Value;

    private async Task InitializeCoreAsync()
    {
        try
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
        catch (Exception ex)
        {
            // Initialization failures are logged but not rethrown, matching the plugin's
            // historical constructor behavior: subsequent operations run against whatever
            // schema exists and surface their own errors. Swallowing here also guarantees
            // the Lazy<Task> gate can never cache a fault that would poison every
            // subsequent operation.
            LogDatabaseInitializationError(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing database")]
    private static partial void LogDatabaseInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping credits for episode {EpisodeId}: detected segment overlaps with introduction")]
    private static partial void LogCreditsOverlapWithIntro(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update timestamp for episode {EpisodeId}")]
    private static partial void LogFailedToUpdateTimestamp(ILogger logger, Exception ex, Guid episodeId);
}
