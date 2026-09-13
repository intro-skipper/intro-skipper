// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Disabled-item operations of <see cref="IntroSkipperDatabase"/>: items whose
/// automatic segments are withheld from Jellyfin.
/// </summary>
internal sealed partial class IntroSkipperDatabase
{
    /// <inheritdoc/>
    public async Task<IReadOnlySet<Guid>> GetDisabledItemIdsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new HashSet<Guid>();
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        return await db.DisabledItems
            .AsNoTracking()
            .Where(e => EF.Parameter(ids).Contains(e.ItemId))
            .Select(e => e.ItemId)
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets whether the item's automatic segments are withheld from Jellyfin, on a
    /// caller-owned context. Stages the flag change without saving; the caller saves
    /// and commits when this answers <see langword="true"/>. An idempotent request
    /// stages nothing and answers <see langword="false"/>.
    /// </summary>
    private static async Task<bool> SetItemDisabledCoreAsync(IntroSkipperDbContext db, Guid itemId, bool disabled, CancellationToken cancellationToken)
    {
        var existing = await db.DisabledItems
            .FindAsync([itemId], cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            if (!disabled)
            {
                return false;
            }

            db.DisabledItems.Add(new DbDisabledItem(itemId));
            return true;
        }

        if (disabled)
        {
            return false;
        }

        db.DisabledItems.Remove(existing);
        return true;
    }
}
