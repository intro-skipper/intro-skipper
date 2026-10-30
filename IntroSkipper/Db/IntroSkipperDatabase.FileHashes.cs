// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Linq.Expressions;
using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// File hash (<see cref="DbFileHash"/>) operations of <see cref="IntroSkipperDatabase"/>
/// and the rebind of orphans.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    // User segments and tombstones: what a person decided about an item.
    private static readonly Expression<Func<DbSegment, bool>> IsHumanRow = s => s.Source == SegmentSource.User || s.State == SegmentState.Suppressed;

    private static readonly Expression<Func<DbSegment, bool>> IsAutomaticRow = s => s.Source != SegmentSource.User && s.State == SegmentState.Active;

    /// <summary>
    /// Reads the stored file hashes of the given items.
    /// </summary>
    /// <param name="itemIds">The item ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stored rows keyed by item id; items without a hash are absent.</returns>
    public async Task<IReadOnlyDictionary<Guid, DbFileHash>> GetFileHashesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        return await db.FileHashes.AsNoTracking()
            .Where(h => EF.Parameter(ids).Contains(h.ItemId))
            .ToDictionaryAsync(h => h.ItemId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Stores file hashes, replacing each item's previous row.
    /// </summary>
    /// <param name="hashes">The rows to store, at most one per item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task SaveFileHashesAsync(IReadOnlyCollection<DbFileHash> hashes, CancellationToken cancellationToken = default)
    {
        if (hashes.Count == 0)
        {
            return Task.CompletedTask;
        }

        var statements = MultiRowSql.Statements(
            hashes,
            h => [h.ItemId, h.SeasonId, h.Hash, h.FileVersion],
            p => $"({p[0]}, {p[1]}, {p[2]}, {p[3]})",
            values => $"""
                INSERT INTO "FileHashes" ("ItemId", "SeasonId", "Hash", "FileVersion")
                VALUES {values}
                ON CONFLICT("ItemId") DO UPDATE SET
                    "SeasonId" = excluded."SeasonId",
                    "Hash" = excluded."Hash",
                    "FileVersion" = excluded."FileVersion"
                """);
        return ExecuteInTransactionAsync(statements, cancellationToken);
    }

    /// <summary>
    /// Reads the rows of every item outside <paramref name="excludedItemIds"/> that holds
    /// one of <paramref name="hashes"/>: the rebind's candidates, orphans and live
    /// duplicates alike.
    /// </summary>
    /// <param name="hashes">The file hashes to look up.</param>
    /// <param name="excludedItemIds">The items asking, which never match themselves.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching rows.</returns>
    public async Task<IReadOnlyList<DbFileHash>> GetFileHashHoldersAsync(
        IReadOnlyCollection<string> hashes,
        IReadOnlyCollection<Guid> excludedItemIds,
        CancellationToken cancellationToken = default)
    {
        var hashArray = hashes.Distinct().ToArray();
        var excluded = excludedItemIds.Distinct().ToArray();
        if (hashArray.Length == 0)
        {
            return [];
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        return await db.FileHashes.AsNoTracking()
            .Where(h => EF.Parameter(hashArray).Contains(h.Hash) && !EF.Parameter(excluded).Contains(h.ItemId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the ids outside <paramref name="retainedItemIds"/> that hold a file hash: the
    /// orphans a rebind could still match.
    /// </summary>
    /// <param name="retainedItemIds">The item ids known to exist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The hashed item ids outside the retained set.</returns>
    public async Task<IReadOnlyList<Guid>> GetHashedItemIdsOutsideAsync(IReadOnlyCollection<Guid> retainedItemIds, CancellationToken cancellationToken = default)
    {
        var retainedIds = retainedItemIds.Distinct().ToArray();
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        return await db.FileHashes.AsNoTracking()
            .Where(h => !EF.Parameter(retainedIds).Contains(h.ItemId))
            .Select(h => h.ItemId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Moves each orphan's rows to its live item in one transaction, so a renamed or
    /// moved file keeps its segments, tombstones and analysis.
    /// </summary>
    /// <remarks>
    /// <para>An item with user segments, tombstones or a disable flag of its own keeps
    /// them and takes nothing. Otherwise the orphan's user segments, tombstones and
    /// disable flag move. Its automatic segments and analysis records move too when the
    /// item has no analysis; when it has, the modes the moved rows cover lose the item's
    /// automatic rows and records, because that analysis was never admitted against
    /// them. A moved record that carried the orphan's media version takes the item's,
    /// since an equal file hash proves the file is the same; a subtitle-versioned record
    /// keeps its value. Whatever stays behind is the orphan's until the cleanup task
    /// forgets it.</para>
    /// <para>Settled-season episode sets name the item instead of the orphan. When the
    /// rebinds cover <paramref name="seasonId"/> and are every hashed item of one gone
    /// season key, that key's analyzer actions, overrides and episode sets move to
    /// <paramref name="seasonId"/>, unless it has state of its own. Otherwise the state
    /// of every gone season key goes: a split or merged season fits no one season, and a
    /// later destination must not take it. The orphans lose their file hash rows, and
    /// both ids journal their projections.</para>
    /// </remarks>
    /// <param name="rebinds">The orphans and the items taking over their rows, one pair per orphan and per item.</param>
    /// <param name="seasonId">The season key the items are analyzed under.</param>
    /// <param name="goneSeasonIds">The season keys the orphans were analyzed under that the server no longer knows, other than <paramref name="seasonId"/>.</param>
    /// <param name="coversSeason"><see langword="true"/> when the rebinds are every item of <paramref name="seasonId"/>; otherwise, <see langword="false"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task RebindItemsAsync(IReadOnlyCollection<ItemRebind> rebinds, Guid seasonId, IReadOnlyList<Guid> goneSeasonIds, bool coversSeason, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // Counted before the orphans' hash rows go: a previous season with more hashed
            // items than these rebinds was split or merged.
            var previous = coversSeason && goneSeasonIds is [var only] && rebinds.All(r => r.Orphan.SeasonId == only) ? only : (Guid?)null;
            var moveState = previous is { } whole
                && await db.FileHashes.CountAsync(h => h.SeasonId == whole, cancellationToken).ConfigureAwait(false) == rebinds.Count
                && !await db.SeasonStates.AnyAsync(s => s.SeasonId == seasonId, cancellationToken).ConfigureAwait(false)
                && !await db.SeasonAnalysisOverrides.AnyAsync(s => s.SeasonId == seasonId, cancellationToken).ConfigureAwait(false);
            foreach (var rebind in rebinds)
            {
                await RebindItemCoreAsync(db, rebind, cancellationToken).ConfigureAwait(false);
            }

            if (moveState)
            {
                await db.Database.ExecuteSqlAsync($"""UPDATE "SeasonStates" SET "SeasonId" = {seasonId} WHERE "SeasonId" = {previous}""", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlAsync($"""UPDATE "SeasonAnalysisOverrides" SET "SeasonId" = {seasonId} WHERE "SeasonId" = {previous}""", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var gone = goneSeasonIds.ToArray();
                await db.SeasonStates.Where(s => EF.Parameter(gone).Contains(s.SeasonId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.SeasonAnalysisOverrides.Where(s => EF.Parameter(gone).Contains(s.SeasonId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            await RenameSettledEpisodesAsync(db, rebinds, seasonId, cancellationToken).ConfigureAwait(false);

            var orphanIds = rebinds.Select(r => r.Orphan.ItemId).ToArray();
            await db.FileHashes
                .Where(h => EF.Parameter(orphanIds).Contains(h.ItemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            await EnqueueProjectionsAsync(db, [.. orphanIds, .. rebinds.Select(r => r.ItemId)], cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RebindItemCoreAsync(IntroSkipperDbContext db, ItemRebind rebind, CancellationToken cancellationToken)
    {
        var (orphan, item) = (rebind.Orphan.ItemId, rebind.ItemId);
        if (await db.Segments.Where(s => s.ItemId == item).AnyAsync(IsHumanRow, cancellationToken).ConfigureAwait(false)
            || await db.DisabledItems.AnyAsync(d => d.ItemId == item, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var itemHasAnalysis = await db.AnalyzedItems.AnyAsync(a => a.ItemId == item, cancellationToken).ConfigureAwait(false)
            || await db.Segments.Where(s => s.ItemId == item).AnyAsync(IsAutomaticRow, cancellationToken).ConfigureAwait(false);
        var intent = db.Segments.Where(s => s.ItemId == orphan).Where(IsHumanRow);
        if (!itemHasAnalysis)
        {
            await db.Segments
                .Where(s => s.ItemId == orphan)
                .Where(IsAutomaticRow)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.ItemId, item), cancellationToken)
                .ConfigureAwait(false);
            await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE "AnalyzedItems" SET "ItemId" = {item},
                    "FileVersion" = CASE WHEN "FileVersion" IS {rebind.Orphan.FileVersion} THEN {rebind.FileVersion} ELSE "FileVersion" END
                WHERE "ItemId" = {orphan}
                """,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var modes = await intent.Select(s => s.Type).Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
            await db.Segments
                .Where(s => s.ItemId == item && modes.Contains(s.Type))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            await db.AnalyzedItems
                .Where(a => a.ItemId == item && modes.Contains(a.Type))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await intent
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.ItemId, item), cancellationToken)
            .ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE "DisabledItems" SET "ItemId" = {item} WHERE "ItemId" = {orphan}""",
            cancellationToken).ConfigureAwait(false);
    }

    // Rewrites the settled-season episode sets of the season keys involved, so a rename
    // does not count as a changed episode set and trigger a settled-season reanalysis.
    private static async Task RenameSettledEpisodesAsync(IntroSkipperDbContext db, IReadOnlyCollection<ItemRebind> rebinds, Guid seasonId, CancellationToken cancellationToken)
    {
        var renames = rebinds.ToDictionary(r => r.Orphan.ItemId, r => r.ItemId);
        var seasonIds = (Guid[])[seasonId, .. rebinds.Select(r => r.Orphan.SeasonId).Distinct()];
        var states = await db.SeasonStates.AsNoTracking()
            .Where(s => EF.Parameter(seasonIds).Contains(s.SeasonId))
            .Select(s => new { s.SeasonId, s.Type, s.SettledReanalysisEpisodeIds })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var state in states.Where(s => s.SettledReanalysisEpisodeIds.Any(renames.ContainsKey)))
        {
            await RecordSettleReanalysisCoreAsync(
                db,
                state.SeasonId,
                [state.Type],
                [.. state.SettledReanalysisEpisodeIds.Select(id => renames.GetValueOrDefault(id, id)).Distinct()],
                cancellationToken).ConfigureAwait(false);
        }
    }
}
