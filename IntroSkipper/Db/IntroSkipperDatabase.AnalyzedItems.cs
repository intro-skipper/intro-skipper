// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
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
    public async Task<int> UpgradeAnalysisHashAsync(
        AnalysisMode mode,
        IReadOnlyCollection<Guid> itemIds,
        string previousHash,
        string currentHash,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0 || string.IsNullOrEmpty(previousHash) || previousHash == currentHash)
        {
            return 0;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var ids = itemIds.Distinct().ToArray();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var completed = db.AnalyzedItems.Where(a => EF.Parameter(ids).Contains(a.ItemId)
                && a.Type == mode && a.ConfigHash == previousHash);

            await db.Segments
                .Where(s => EF.Parameter(ids).Contains(s.ItemId)
                    && completed.Any(a => a.ItemId == s.ItemId)
                    && s.State == SegmentState.Active
                    && s.Source != SegmentSource.User
                    && s.ConfigHash == previousHash
                    && ((s.Source != SegmentSource.CreditsDerived && s.Type == mode)
                        || (s.Source == SegmentSource.CreditsDerived && mode == AnalysisMode.Credits)))
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.ConfigHash, currentHash), cancellationToken)
                .ConfigureAwait(false);

            var updated = await completed
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.ConfigHash, currentHash), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
    }

    /// <inheritdoc/>
    public Task MarkItemsAnalyzedAsync(AnalysisMode mode, IEnumerable<(Guid ItemId, long? FileVersion)> items, string configHash, CancellationToken cancellationToken = default)
    {
        var rows = items.DistinctBy(item => item.ItemId).ToArray();
        if (rows.Length == 0)
        {
            return Task.CompletedTask;
        }

        // {0} binds the mode and {1} the hash once per statement; every row reuses them.
        var statements = MultiRowSql.Statements(
            rows,
            item => [item.ItemId, item.FileVersion],
            p => $"({p[0]}, {{0}}, {{1}}, {p[1]})",
            values => $"""
                INSERT INTO "AnalyzedItems" ("ItemId", "Type", "ConfigHash", "FileVersion")
                VALUES {values}
                ON CONFLICT("ItemId", "Type") DO UPDATE SET "ConfigHash" = excluded."ConfigHash", "FileVersion" = excluded."FileVersion"
                """,
            (int)mode,
            configHash);
        return ExecuteInTransactionAsync(statements, cancellationToken);
    }

    /// <inheritdoc/>
    public Task BackfillFileVersionsAsync(IReadOnlyDictionary<Guid, long> fileVersionsByItem, CancellationToken cancellationToken = default)
    {
        if (fileVersionsByItem.Count == 0)
        {
            return Task.CompletedTask;
        }

        // A VALUES table exposes its columns as column1, column2 in SQLite, in the order
        // the row lambda binds them.
        var statements = MultiRowSql.Statements(
            fileVersionsByItem,
            item => [item.Key, item.Value],
            p => $"({p[0]}, {p[1]})",
            values => $"""
                UPDATE "AnalyzedItems" SET "FileVersion" = "v"."column2"
                FROM (VALUES {values}) AS "v"
                WHERE "AnalyzedItems"."ItemId" = "v"."column1" AND "AnalyzedItems"."FileVersion" IS NULL
                """);
        return ExecuteInTransactionAsync(statements, cancellationToken);
    }

    /// <summary>
    /// Runs the chunked statements of one analysis-record write in a single transaction,
    /// so a cancelled pass cannot leave a season half-recorded.
    /// </summary>
    private async Task ExecuteInTransactionAsync(IEnumerable<FormattableString> statements, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            foreach (var statement in statements)
            {
                await db.Database.ExecuteSqlAsync(statement, cancellationToken).ConfigureAwait(false);
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
