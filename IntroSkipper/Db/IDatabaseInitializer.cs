// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Owns the lifecycle of both plugin databases: applying EF migrations and legacy schema repair to
/// the segment database, ensuring the detection cache schema exists (including delete-and-recreate
/// corruption recovery), and rebuilding the segment database on demand.
/// Initialization is idempotent and runs at most once per process; the <c>Ensure*</c> members act as
/// gates that all stores await before touching a database, which guarantees no query can observe a
/// database that has not been migrated yet.
/// </summary>
public interface IDatabaseInitializer
{
    /// <summary>
    /// Ensures legacy schema repair and EF migrations have completed for the segment database.
    /// The first caller triggers initialization; concurrent and subsequent callers await the same
    /// task. Initialization failures are logged and swallowed (matching the historical behavior of
    /// the <see cref="Plugin"/> constructor) so a broken database degrades to per-query errors
    /// instead of poisoning every future call.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. Cancels only this caller's wait, never the shared initialization work.</param>
    /// <returns>A task that completes when initialization has finished.</returns>
    Task EnsureSegmentDbReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the detection cache database schema exists, recreating the database when it is
    /// corrupted. Synchronous because all detection cache store operations are synchronous.
    /// </summary>
    void EnsureCacheDbReady();

    /// <summary>
    /// Rebuilds the segment database while attempting to preserve valid segments and season state.
    /// </summary>
    /// <param name="forceCleanOnBackupFailure">
    /// When <see langword="true"/>, the rebuild proceeds with an empty database if the backup read
    /// fails; when <see langword="false"/>, the rebuild aborts to avoid data loss.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RebuildSegmentDatabaseAsync(bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default);
}
