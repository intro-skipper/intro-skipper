// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading.Tasks;
using IntroSkipper.Db;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestDisabledItems
{
    [Fact]
    public async Task SetItemDisabledAsync_RoundTripsThroughGetAndIs()
    {
        var dbPath = DatabaseTestHelpers.CreateTempDbPath("disabled-items-roundtrip.db");
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var seasonId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            await database.SetItemDisabledAsync(seasonId, itemId, disabled: true);

            Assert.True(await database.IsItemDisabledAsync(itemId));
            Assert.Equal([itemId], await database.GetDisabledItemIdsAsync(seasonId));

            await database.SetItemDisabledAsync(seasonId, itemId, disabled: false);

            Assert.False(await database.IsItemDisabledAsync(itemId));
            Assert.Empty(await database.GetDisabledItemIdsAsync(seasonId));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task SetItemDisabledAsync_IsIdempotentInBothDirections()
    {
        var dbPath = DatabaseTestHelpers.CreateTempDbPath("disabled-items-idempotent.db");
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var seasonId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            // Disabling an already-enabled state twice must not duplicate the row,
            // enabling an absent row must not throw.
            await database.SetItemDisabledAsync(seasonId, itemId, disabled: false);
            await database.SetItemDisabledAsync(seasonId, itemId, disabled: true);
            await database.SetItemDisabledAsync(seasonId, itemId, disabled: true);

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.Equal(1, await db.DisabledItems.CountAsync(e => e.ItemId == itemId));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task IsItemDisabledAsync_MatchesRegardlessOfSeasonKey()
    {
        var dbPath = DatabaseTestHelpers.CreateTempDbPath("disabled-items-season-agnostic.db");
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var itemId = Guid.NewGuid();

            await database.SetItemDisabledAsync(Guid.NewGuid(), itemId, disabled: true);

            // The sync path only knows the item id, not which season key owns the flag.
            Assert.True(await database.IsItemDisabledAsync(itemId));
            Assert.False(await database.IsItemDisabledAsync(Guid.NewGuid()));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task CleanSeasonStateAsync_PrunesDisabledItemsOfDroppedSeasons()
    {
        var dbPath = DatabaseTestHelpers.CreateTempDbPath("disabled-items-cleanup.db");
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var retainedSeasonId = Guid.NewGuid();
            var retainedItemId = Guid.NewGuid();
            var movieId = Guid.NewGuid();
            var droppedSeasonId = Guid.NewGuid();
            var droppedItemId = Guid.NewGuid();

            await database.SetItemDisabledAsync(retainedSeasonId, retainedItemId, disabled: true);

            // A movie is queued under its own id, so its id is a retained season key.
            await database.SetItemDisabledAsync(movieId, movieId, disabled: true);
            await database.SetItemDisabledAsync(droppedSeasonId, droppedItemId, disabled: true);

            await database.CleanSeasonStateAsync([retainedSeasonId, movieId]);

            Assert.True(await database.IsItemDisabledAsync(retainedItemId));
            Assert.True(await database.IsItemDisabledAsync(movieId));
            Assert.False(await database.IsItemDisabledAsync(droppedItemId));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task RebuildDatabaseAsync_PreservesDisabledItems()
    {
        var dbPath = DatabaseTestHelpers.CreateTempDbPath("disabled-items-rebuild.db");
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var seasonId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            await database.SetItemDisabledAsync(seasonId, itemId, disabled: true);

            await database.RebuildDatabaseAsync();

            Assert.True(await database.IsItemDisabledAsync(itemId));
            Assert.Equal([itemId], await database.GetDisabledItemIdsAsync(seasonId));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }
}
