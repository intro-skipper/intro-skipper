// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Explicit episode-disable operations of <see cref="IntroSkipperDatabase"/>.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <inheritdoc/>
    public async Task<IReadOnlySet<Guid>> GetDisabledEpisodeIdsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var ids = await db.DbDisabledEpisode
            .AsNoTracking()
            .Where(e => e.SeasonId == seasonId)
            .Select(e => e.EpisodeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.ToHashSet();
    }

    /// <inheritdoc/>
    public async Task SetEpisodeAnalysisDisabledAsync(Guid seasonId, Guid episodeId, bool disabled, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var existing = await db.DbDisabledEpisode
            .FindAsync([seasonId, episodeId], cancellationToken)
            .ConfigureAwait(false);

        if (disabled)
        {
            if (existing is null)
            {
                db.DbDisabledEpisode.Add(new DbDisabledEpisode(seasonId, episodeId));
            }
        }
        else if (existing is not null)
        {
            db.DbDisabledEpisode.Remove(existing);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> IsEpisodeAnalysisDisabledAsync(Guid episodeId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        return await db.DbDisabledEpisode
            .AsNoTracking()
            .AnyAsync(e => e.EpisodeId == episodeId, cancellationToken)
            .ConfigureAwait(false);
    }
}
