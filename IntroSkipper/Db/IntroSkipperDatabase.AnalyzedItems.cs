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
    private const string AnalysisRecheckRequestKey = "analysis-recheck-request";

    /// <summary>
    /// Re-stamps completed analysis and its active automatic segments from one hash to
    /// another in one transaction, for the 10.11 adoption and the chapter source check. Only
    /// records still carrying the previous hash are updated; missing or reset records are
    /// never recreated. Segment payloads stay unchanged.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="itemIds">Previously completed item IDs.</param>
    /// <param name="previousHash">The hash the records carry now.</param>
    /// <param name="currentHash">The hash the pass would write for them.</param>
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
    /// Records the items as analyzed for the mode under both record hashes and each item's file
    /// version, whether or not segments were found, replacing any earlier record of the same item
    /// and mode.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="items">Items that were analyzed, each with the file version it was analyzed at (null when unknown).</param>
    /// <param name="hashes">The configuration and keep hashes the mode ran under.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the records are committed in one transaction.</returns>
    public Task MarkItemsAnalyzedAsync(AnalysisMode mode, IEnumerable<(Guid ItemId, long? FileVersion)> items, RecordHashes hashes, CancellationToken cancellationToken = default)
    {
        var rows = items.DistinctBy(item => item.ItemId).ToArray();
        if (rows.Length == 0)
        {
            return Task.CompletedTask;
        }

        var statements = MultiRowSql.Statements(
            rows,
            item => [item.ItemId, item.FileVersion],
            p => $"({p[0]}, {{0}}, {{1}}, {p[1]}, {{2}})",
            values => $"""
                INSERT INTO "AnalyzedItems" ("ItemId", "Type", "ConfigHash", "FileVersion", "KeepHash")
                VALUES {values}
                ON CONFLICT("ItemId", "Type") DO UPDATE SET "ConfigHash" = excluded."ConfigHash", "FileVersion" = excluded."FileVersion", "KeepHash" = excluded."KeepHash"
                """,
            (int)mode,
            hashes.ConfigHash,
            hashes.KeepHash);
        return ExecuteInTransactionAsync(statements, cancellationToken);
    }

    /// <summary>
    /// Stores the keep hash on records queue verification settled under another keep hash.
    /// Updates only rows that still carry the configuration hash they were judged under, so a
    /// record rewritten between the snapshot and this call keeps its own state. Creates no
    /// missing record.
    /// </summary>
    /// <param name="confirmations">The records to confirm, grouped by mode and hashes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when every confirmation is committed in one transaction.</returns>
    public async Task ConfirmKeepHashesAsync(IReadOnlyCollection<KeepConfirmation> confirmations, CancellationToken cancellationToken = default)
    {
        if (confirmations.Count == 0)
        {
            return;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            foreach (var (mode, hashes, itemIds) in confirmations)
            {
                var ids = itemIds.Distinct().ToArray();
                await db.AnalyzedItems
                    .Where(a => a.Type == mode && a.ConfigHash == hashes.ConfigHash && EF.Parameter(ids).Contains(a.ItemId))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.KeepHash, hashes.KeepHash), cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the current re-check request, or an empty string when none was ever made.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The request token.</returns>
    public async Task<string> GetAnalysisRecheckRequestAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        return await db.PluginMetadata
            .AsNoTracking()
            .Where(row => row.Key == AnalysisRecheckRequestKey)
            .Select(row => row.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? string.Empty;
    }

    /// <summary>
    /// Stores a new re-check request. Every record whose keep hash carries an older request is
    /// judged by its configuration hash on the next pass over its season. So is every record
    /// without a keep hash: the same transaction gives it an empty one, which matches no keep
    /// hash, so the verifier does not keep it as it stands under the new request.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the request and the marks are committed in one transaction.</returns>
    public Task RequestAnalysisRecheckAsync(CancellationToken cancellationToken = default)
    {
        var request = Guid.CreateVersion7().ToString("N");
        FormattableString upsert = $"""
            INSERT INTO "PluginMetadata" ("Key", "Value")
            VALUES ({AnalysisRecheckRequestKey}, {request})
            ON CONFLICT("Key") DO UPDATE SET "Value" = excluded."Value"
            """;
        FormattableString markWithoutKeepHash = $"""UPDATE "AnalyzedItems" SET "KeepHash" = '' WHERE "KeepHash" IS NULL""";
        return ExecuteInTransactionAsync([upsert, markWithoutKeepHash], cancellationToken);
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
