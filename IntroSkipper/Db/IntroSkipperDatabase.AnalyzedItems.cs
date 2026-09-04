// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Per-item analysis record (<see cref="DbAnalyzedItem"/>) operations of <see cref="IntroSkipperDatabase"/>.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <inheritdoc/>
    public async Task MarkItemsAnalyzedAsync(AnalysisMode mode, IEnumerable<Guid> itemIds, string configHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ArgumentNullException.ThrowIfNull(configHash);

        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // One transaction so a cancelled pass cannot leave the season half-recorded.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            foreach (var id in ids)
            {
                await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO "AnalyzedItems" ("ItemId", "Type", "ConfigHash")
                    VALUES ({id}, {(int)mode}, {configHash})
                    ON CONFLICT("ItemId", "Type") DO UPDATE SET "ConfigHash" = excluded."ConfigHash"
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Single-shot form of <see cref="ClearItemAnalysisCoreAsync"/>: removes an
    /// item's analysis record for the mode so the next scan analyzes it again (a
    /// no-op when no record exists). Internal on purpose — production callers reach
    /// the core through <see cref="ApplyChangeAsync"/>; this is the test seam over
    /// the same core.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal async Task ClearItemAnalysisAsync(Guid itemId, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        await ClearItemAnalysisCoreAsync(db, itemId, mode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core of <see cref="ClearItemAnalysisAsync"/> on a caller-owned context. The
    /// delete executes immediately (not staged), scoped by any ambient transaction.
    /// </summary>
    private static async Task ClearItemAnalysisCoreAsync(IntroSkipperDbContext db, Guid itemId, AnalysisMode mode, CancellationToken cancellationToken)
    {
        await db.AnalyzedItems
            .Where(a => a.ItemId == itemId && a.Type == mode)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
