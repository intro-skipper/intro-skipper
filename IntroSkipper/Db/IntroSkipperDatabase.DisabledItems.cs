// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Disabled-item operations of <see cref="IntroSkipperDatabase"/>: items whose
/// automatic segments are withheld from Jellyfin.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <inheritdoc/>
    public async Task<IReadOnlySet<Guid>> GetDisabledItemIdsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var ids = await db.DisabledItems
            .AsNoTracking()
            .Where(e => e.SeasonId == seasonId)
            .Select(e => e.ItemId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ids.ToHashSet();
    }

    /// <inheritdoc/>
    public async Task SetItemDisabledAsync(Guid seasonId, Guid itemId, bool disabled, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var existing = await db.DisabledItems
            .FindAsync([seasonId, itemId], cancellationToken)
            .ConfigureAwait(false);

        if (disabled == (existing is not null))
        {
            return;
        }

        if (disabled)
        {
            db.DisabledItems.Add(new DbDisabledItem(seasonId, itemId));
        }
        else
        {
            db.DisabledItems.Remove(existing!);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> IsItemDisabledAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        return await db.DisabledItems
            .AsNoTracking()
            .AnyAsync(e => e.ItemId == itemId, cancellationToken)
            .ConfigureAwait(false);
    }
}
