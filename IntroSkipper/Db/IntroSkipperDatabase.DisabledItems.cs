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

        return await db.DisabledItems
            .AsNoTracking()
            .Where(e => e.SeasonId == seasonId)
            .Select(e => e.ItemId)
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> SetItemDisabledAsync(Guid seasonId, Guid itemId, bool disabled, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var existing = await db.DisabledItems
            .FindAsync([itemId], cancellationToken)
            .ConfigureAwait(false);
        var previous = existing is not null;

        if (disabled)
        {
            if (existing is null)
            {
                db.DisabledItems.Add(new DbDisabledItem(seasonId, itemId));
            }
            else if (existing.SeasonId == seasonId)
            {
                return previous;
            }
            else
            {
                // The item moved season keys since it was disabled; the flag
                // follows the item, so rewrite the stale key in place.
                existing.SeasonId = seasonId;
            }
        }
        else
        {
            if (existing is null)
            {
                return previous;
            }

            db.DisabledItems.Remove(existing);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return previous;
    }
}
