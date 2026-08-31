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

    /// <summary>
    /// Single-shot form of <see cref="SetItemDisabledCoreAsync"/>: sets whether the
    /// item's automatic segments are withheld from Jellyfin (disabling rewrites a
    /// stale season key in place; idempotent requests write nothing) and returns the
    /// previous flag. Internal on purpose — it does not journal a projection, so
    /// production writes go through <see cref="ApplyChangeAsync"/>; this is the
    /// domain-semantics test seam over the same core.
    /// </summary>
    /// <param name="seasonId">Season-state key that owns the item (a movie's own ID for movies).</param>
    /// <param name="itemId">Item ID.</param>
    /// <param name="disabled">Whether to withhold the item's automatic segments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the item was disabled before this write.</returns>
    internal async Task<bool> SetItemDisabledAsync(Guid seasonId, Guid itemId, bool disabled, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var (previous, changed) = await SetItemDisabledCoreAsync(db, seasonId, itemId, disabled, cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return previous;
    }

    /// <summary>
    /// Core of <see cref="SetItemDisabledAsync"/> on a caller-owned context. Stages the
    /// flag change without saving; the caller saves and commits when <c>Changed</c> is set.
    /// </summary>
    private static async Task<(bool Previous, bool Changed)> SetItemDisabledCoreAsync(
        IntroSkipperDbContext db,
        Guid seasonId,
        Guid itemId,
        bool disabled,
        CancellationToken cancellationToken)
    {
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
                return (previous, false);
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
                return (previous, false);
            }

            db.DisabledItems.Remove(existing);
        }

        return (previous, true);
    }
}
