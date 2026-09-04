// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Per-item analysis record (<see cref="DbAnalyzedItem"/>) operations of <see cref="IntroSkipperDatabase"/>.
/// </summary>
internal sealed partial class IntroSkipperDatabase
{
    /// <inheritdoc/>
    public async Task MarkItemsAnalyzedAsync(AnalysisMode mode, IEnumerable<Guid> itemIds, string configHash, CancellationToken cancellationToken = default)
    {
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
    /// Removes an item's analysis record for the mode so the next scan analyzes it
    /// again (a no-op when no record exists). The delete executes immediately (not
    /// staged), scoped by any ambient transaction of the caller-owned context.
    /// </summary>
    private static async Task ClearItemAnalysisCoreAsync(IntroSkipperDbContext db, Guid itemId, AnalysisMode mode, CancellationToken cancellationToken)
    {
        await db.AnalyzedItems
            .Where(a => a.ItemId == itemId && a.Type == mode)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
