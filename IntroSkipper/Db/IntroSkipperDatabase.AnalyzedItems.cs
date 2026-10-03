// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Per-item analysis record (<see cref="DbAnalyzedItem"/>) operations of <see cref="IntroSkipperDatabase"/>.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <summary>
    /// Atomically adopts completed analysis and its active automatic segments under a
    /// compatible hash. Only records still carrying the previous hash are updated;
    /// missing or reset records are never recreated. Segment payloads stay unchanged.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="itemIds">Previously completed item IDs.</param>
    /// <param name="previousHash">Recognized legacy hash.</param>
    /// <param name="currentHash">Compatible current hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of completion records updated.</returns>
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

    /// <summary>
    /// Records the items as analyzed for the mode under the given configuration hash and
    /// each item's file version, whether or not segments were found, replacing any earlier
    /// record of the same item and mode. Queue verification treats a matching record as
    /// settled (<c>Analyzed</c> with segments, <c>NoSegments</c> without) and a missing or
    /// mismatching one as <c>NotAnalyzed</c>.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="items">Items that were analyzed, each with the file version it was analyzed at (null when unknown).</param>
    /// <param name="configHash">Configuration hash used for the analysis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task MarkItemsAnalyzedAsync(AnalysisMode mode, IEnumerable<(Guid ItemId, long? FileVersion)> items, string configHash, CancellationToken cancellationToken = default)
        => MarkItemsAnalyzedAsync(
            mode,
            items.Select(item => (item.ItemId, item.FileVersion, (string?)null, (double?)null)),
            configHash,
            cancellationToken);

    /// <inheritdoc/>
    public Task MarkItemsAnalyzedAsync(AnalysisMode mode, IEnumerable<(Guid ItemId, long? FileVersion, string? ShortcutPath, double? Duration)> items, string configHash, CancellationToken cancellationToken = default)
    {
        var rows = items.DistinctBy(item => item.ItemId).ToArray();
        if (rows.Length == 0)
        {
            return Task.CompletedTask;
        }

        // {0} binds the mode and {1} the hash once per statement; every row reuses them.
        var statements = MultiRowSql.Statements(
            rows,
            item => [item.ItemId, item.FileVersion, item.ShortcutPath, item.Duration],
            p => $"({p[0]}, {{0}}, {{1}}, {p[1]}, {p[2]}, {p[3]})",
            values => $"""
                INSERT INTO "AnalyzedItems" ("ItemId", "Type", "ConfigHash", "FileVersion", "ShortcutPath", "Duration")
                VALUES {values}
                ON CONFLICT("ItemId", "Type") DO UPDATE SET "ConfigHash" = excluded."ConfigHash", "FileVersion" = excluded."FileVersion", "ShortcutPath" = excluded."ShortcutPath", "Duration" = excluded."Duration"
                """,
            (int)mode,
            configHash);
        return ExecuteInTransactionAsync(statements, cancellationToken);
    }

    /// <summary>
    /// Stamps the given file version on every analysis record of each item that has none
    /// yet. Records written before versioning match any file; stamping them with the
    /// version seen at verification lets a later replacement of the file be noticed.
    /// Records that already carry a version are left alone.
    /// </summary>
    /// <param name="fileVersionsByItem">The file version to stamp on each item's unversioned records.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
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
