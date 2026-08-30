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

        // Delete-then-insert inside one transaction is the upsert: a tracked insert over
        // an existing composite key would fail, and one statement per set beats a
        // read-modify-write per item.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await db.AnalyzedItems
                .Where(a => a.Type == mode && EF.Parameter(ids).Contains(a.ItemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            db.AnalyzedItems.AddRange(ids.Select(id => new DbAnalyzedItem(id, mode, configHash)));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task ClearItemAnalysisAsync(Guid itemId, AnalysisMode mode, CancellationToken cancellationToken = default)
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
