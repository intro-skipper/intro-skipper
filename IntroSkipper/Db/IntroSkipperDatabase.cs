// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Default implementation of <see cref="IIntroSkipperDatabase"/>.
/// The implementation is split across partial class files by concern:
/// <list type="bullet">
/// <item><description><c>IntroSkipperDatabase.cs</c>: lifecycle (initialization gate, migrations, rebuild).</description></item>
/// <item><description><c>IntroSkipperDatabase.Segments.cs</c>: <see cref="DbSegment"/> reads and writes.</description></item>
/// <item><description><c>IntroSkipperDatabase.SeasonStates.cs</c>: <see cref="DbSeasonState"/> reads and writes.</description></item>
/// <item><description><c>IntroSkipperDatabase.Maintenance.cs</c>: bulk cleanup operations spanning both tables.</description></item>
/// </list>
/// The facade is stateless apart from the retryable initialization gate: every operation
/// creates a fresh <see cref="IntroSkipperDbContext"/> from the injected factory, exactly
/// as the previous <c>Plugin</c>-hosted methods did.
/// </summary>
public sealed partial class IntroSkipperDatabase : IIntroSkipperDatabase
{
    // Seconds-based tolerance for treating two segment ranges as the same entry. Shared
    // with consumers that must match rows the facade wrote (e.g. the editor's
    // user-provided-flag resolution) so the matching contract cannot drift.
    internal const double SegmentComparisonEpsilon = 0.001;

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
        // Task.Run is load-bearing, not redundant: InitializeCoreAsync begins with fully
        // synchronous work (EnsureLegacySchemaCompatibility can rebuild whole tables on
        // large legacy databases), so invoking the factory inline would make every
        // concurrent first-touch caller - including purely async ones on the playback
        // hot path - block its thread on the Lazy monitor until the factory's first
        // incomplete await. Dispatching to the thread pool makes the factory return a
        // Task immediately, so waiters genuinely await instead of blocking.
        _initialization = new RetryableInitializationGate<Task>(() => Task.Run(InitializeCoreAsync));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every public data operation awaits this first, which guarantees that no query can
    /// observe the database before legacy schema repair and EF migrations have completed
    /// regardless of whether the eager initializer (hosted service) has already run.
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

    /// <summary>
    /// Determines whether two second-based ranges are the same entry within
    /// <see cref="SegmentComparisonEpsilon"/>.
    /// </summary>
    /// <remarks>
    /// Callers that validate a write before it reaches the facade must use this so their
    /// answer cannot disagree with the guard that rejects the write. Queries translated to
    /// SQL keep the comparison inline: EF Core cannot translate a method call.
    /// </remarks>
    /// <param name="startSeconds">First range start, in seconds.</param>
    /// <param name="endSeconds">First range end, in seconds.</param>
    /// <param name="otherStartSeconds">Second range start, in seconds.</param>
    /// <param name="otherEndSeconds">Second range end, in seconds.</param>
    /// <returns><see langword="true"/> when both bounds match within the tolerance.</returns>
    internal static bool RangesEquivalent(double startSeconds, double endSeconds, double otherStartSeconds, double otherEndSeconds)
        => Math.Abs(startSeconds - otherStartSeconds) <= SegmentComparisonEpsilon
            && Math.Abs(endSeconds - otherEndSeconds) <= SegmentComparisonEpsilon;

    /// <summary>
    /// Determines whether two tick-based ranges are the same entry within
    /// <see cref="SegmentComparisonEpsilon"/>.
    /// </summary>
    /// <param name="startTicks">First range start, in ticks.</param>
    /// <param name="endTicks">First range end, in ticks.</param>
    /// <param name="otherStartTicks">Second range start, in ticks.</param>
    /// <param name="otherEndTicks">Second range end, in ticks.</param>
    /// <returns><see langword="true"/> when both bounds match within the tolerance.</returns>
    internal static bool TickRangesEquivalent(long startTicks, long endTicks, long otherStartTicks, long otherEndTicks)
        => RangesEquivalent(
            TimeSpan.FromTicks(startTicks).TotalSeconds,
            TimeSpan.FromTicks(endTicks).TotalSeconds,
            TimeSpan.FromTicks(otherStartTicks).TotalSeconds,
            TimeSpan.FromTicks(otherEndTicks).TotalSeconds);

    private async Task InitializeCoreAsync()
    {
        using var db = _contextFactory.CreateDbContext();

        // Legacy databases may be missing migration history or columns that EF migrations
        // expect. Normalize those schemas first so recovery does not log a false
        // initialization failure.
        db.EnsureLegacySchemaCompatibility();
        await db.ApplyMigrationsAsync().ConfigureAwait(false);

        await SqlitePragmas.EnforceWalAsync(db.Database).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database initialization failed; the next database operation will retry")]
    private static partial void LogDatabaseInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping credits for episode {EpisodeId}: detected segment overlaps with introduction")]
    private static partial void LogCreditsOverlapWithIntro(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update timestamp for episode {EpisodeId}")]
    private static partial void LogFailedToUpdateTimestamp(ILogger logger, Exception ex, Guid episodeId);
}
