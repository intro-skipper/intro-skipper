// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestDisabledItems
{
    [Fact]
    public async Task SetItemDisabled_RoundTripsThroughGet()
    {
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var itemId = Guid.NewGuid();

        await database.SetItemDisabledAsync(itemId, disabled: true);

        Assert.Equal([itemId], await database.GetDisabledItemIdsAsync([itemId, Guid.NewGuid()]));

        await database.SetItemDisabledAsync(itemId, disabled: false);

        Assert.Empty(await database.GetDisabledItemIdsAsync([itemId]));
    }

    [Fact]
    public async Task SetItemDisabled_IsIdempotentInBothDirections()
    {
        var dbPath = DatabaseTestHelpers.CreateTempDbPath("disabled-items-idempotent.db");
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var itemId = Guid.NewGuid();

            // Disabling an already-enabled state twice must not duplicate the row,
            // enabling an absent row must not throw.
            await database.SetItemDisabledAsync(itemId, disabled: false);
            await database.SetItemDisabledAsync(itemId, disabled: true);
            await database.SetItemDisabledAsync(itemId, disabled: true);

            await using var db = DatabaseTestHelpers.CreateSegmentContext(dbPath);
            Assert.Equal(1, await db.DisabledItems.CountAsync(e => e.ItemId == itemId));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task MigratedSchema_EnforcesOneRowPerItem()
    {
        var dbPath = DatabaseTestHelpers.CreateTempDbPath("disabled-items-migrated-pk.db");
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var itemId = Guid.NewGuid();

            await database.SetItemDisabledAsync(itemId, disabled: true);

            // Raw insert against the migrated file: the migration's DDL, not just
            // the EF model, must enforce the one-row-per-item invariant.
            await using var db = DatabaseTestHelpers.CreateSegmentContext(dbPath);
            db.DisabledItems.Add(new DbDisabledItem(itemId));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetServableSegmentsAsync_WithholdsAutomaticRowsWhileDisabled()
    {
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var itemId = Guid.NewGuid();

        var intro = new Segment(itemId, new TimeRange(0, 30));
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [intro], SegmentSource.Chromaprint);
        await database.SeedUserSegmentAsync(itemId, AnalysisMode.Credits, TickConversions.FromSeconds(60), TickConversions.FromSeconds(90));

        Assert.Equal(2, (await database.GetServableSegmentsAsync(itemId)).Count);

        await database.SetItemDisabledAsync(itemId, disabled: true);

        var served = await database.GetServableSegmentsAsync(itemId);
        Assert.Equal(SegmentSource.User, Assert.Single(served).Source);

        // The editor's storage view keeps both rows.
        Assert.Equal(2, (await database.GetSegmentsAsync(itemId)).Count);
    }

    [Fact]
    public async Task CleanItemStateAsync_PrunesByItemId()
    {
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var retainedItemId = Guid.NewGuid();
        var removedItemId = Guid.NewGuid();
        await database.SetItemDisabledAsync(retainedItemId, disabled: true);
        await database.SetItemDisabledAsync(removedItemId, disabled: true);

        await database.CleanItemStateAsync([retainedItemId]);

        Assert.Equal([retainedItemId], await database.GetDisabledItemIdsAsync([retainedItemId, removedItemId]));
    }
}
