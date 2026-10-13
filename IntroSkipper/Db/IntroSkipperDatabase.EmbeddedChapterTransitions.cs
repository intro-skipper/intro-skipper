// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Durable chapter-source transition state for <see cref="IntroSkipperDatabase"/>.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    private const int EmbeddedChapterTransitionId = 1;

    /// <summary>
    /// Gets the persisted chapter-source transition state, or <see langword="null"/>
    /// when this version has not recorded one yet.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted transition state.</returns>
    internal async Task<EmbeddedChapterTransitionState?> GetEmbeddedChapterTransitionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var state = await db.EmbeddedChapterTransitions
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == EmbeddedChapterTransitionId, cancellationToken)
            .ConfigureAwait(false);

        return state is null
            ? null
            : new EmbeddedChapterTransitionState(state.AppliedEmbeddedChapters, state.PendingEmbeddedChapters);
    }

    /// <summary>
    /// Records that a chapter-source transition has started. The applied value is
    /// deliberately left unchanged so an interrupted pass remains recoverable.
    /// </summary>
    /// <param name="embedded">Whether the transition targets embedded chapters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal Task SetEmbeddedChapterTransitionPendingAsync(bool embedded, CancellationToken cancellationToken = default)
        => ExecuteEmbeddedChapterTransitionSqlAsync(
            $"""
            INSERT INTO "EmbeddedChapterTransitions" ("Id", "AppliedEmbeddedChapters", "PendingEmbeddedChapters")
            VALUES ({EmbeddedChapterTransitionId}, 0, {embedded})
            ON CONFLICT("Id") DO UPDATE SET "PendingEmbeddedChapters" = excluded."PendingEmbeddedChapters"
            """,
            cancellationToken);

    /// <summary>
    /// Records a successfully completed chapter-source transition and clears its pending marker.
    /// </summary>
    /// <param name="embedded">Whether the completed pass used embedded chapters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal Task SetEmbeddedChapterTransitionAppliedAsync(bool embedded, CancellationToken cancellationToken = default)
        => ExecuteEmbeddedChapterTransitionSqlAsync(
            $"""
            INSERT INTO "EmbeddedChapterTransitions" ("Id", "AppliedEmbeddedChapters", "PendingEmbeddedChapters")
            VALUES ({EmbeddedChapterTransitionId}, {embedded}, NULL)
            ON CONFLICT("Id") DO UPDATE SET
                "AppliedEmbeddedChapters" = excluded."AppliedEmbeddedChapters",
                "PendingEmbeddedChapters" = NULL
            """,
            cancellationToken);

    private async Task ExecuteEmbeddedChapterTransitionSqlAsync(FormattableString statement, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        await db.Database.ExecuteSqlAsync(statement, cancellationToken).ConfigureAwait(false);
    }
}
