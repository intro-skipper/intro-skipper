// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
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
    public async Task SetItemDisabledAsync_RoundTripsThroughGet()
    {
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var seasonId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        await database.SetItemDisabledAsync(seasonId, itemId, disabled: true);

        Assert.Equal([itemId], await database.GetDisabledItemIdsAsync(seasonId));

        await database.SetItemDisabledAsync(seasonId, itemId, disabled: false);

        Assert.Empty(await database.GetDisabledItemIdsAsync(seasonId));
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
    public async Task SetItemDisabledAsync_RewritesSeasonKeyOnDrift()
    {
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var itemId = Guid.NewGuid();
        var oldSeasonId = Guid.NewGuid();
        var newSeasonId = Guid.NewGuid();

        await database.SetItemDisabledAsync(oldSeasonId, itemId, disabled: true);

        // The item's queue key drifted (e.g. an in-season special regrouped):
        // a disable write under the new key moves the flag instead of forking it.
        await database.SetItemDisabledAsync(newSeasonId, itemId, disabled: true);

        Assert.Empty(await database.GetDisabledItemIdsAsync(oldSeasonId));
        Assert.Equal([itemId], await database.GetDisabledItemIdsAsync(newSeasonId));

        // Enabling removes the flag by item id, no matter which key recorded it.
        await database.SetItemDisabledAsync(Guid.NewGuid(), itemId, disabled: false);
        Assert.Empty(await database.GetDisabledItemIdsAsync(newSeasonId));
    }

    [Fact]
    public async Task MigratedSchema_EnforcesOneRowPerItem()
    {
        var dbPath = DatabaseTestHelpers.CreateTempDbPath("disabled-items-migrated-pk.db");
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var itemId = Guid.NewGuid();

            await database.SetItemDisabledAsync(Guid.NewGuid(), itemId, disabled: true);

            // Raw insert against the migrated file: the migration's DDL, not just
            // the EF model, must enforce the one-row-per-item invariant.
            await using var db = new IntroSkipperDbContext(dbPath);
            db.DisabledItems.Add(new DbDisabledItem(Guid.NewGuid(), itemId));

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
        var seasonId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var intro = new Segment(itemId, new TimeRange(0, 30));
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [intro], SegmentSource.Chromaprint);
        await database.AddUserSegmentAsync(itemId, AnalysisMode.Credits, TickConversions.FromSeconds(60), TickConversions.FromSeconds(90));

        Assert.Equal(2, (await database.GetServableSegmentsAsync(itemId)).Count);

        await database.SetItemDisabledAsync(seasonId, itemId, disabled: true);

        var served = await database.GetServableSegmentsAsync(itemId);
        Assert.Equal(SegmentSource.User, Assert.Single(served).Source);

        // The editor's storage view keeps both rows.
        Assert.Equal(2, (await database.GetSegmentsAsync(itemId)).Count);
    }

    [Fact]
    public async Task CleanItemStateAsync_PrunesByItemId_NotByStoredSeasonKey()
    {
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var retainedItemId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var removedItemId = Guid.NewGuid();
        var removedSeasonId = Guid.NewGuid();
        var staleSeasonId = Guid.NewGuid();

        // The retained item was disabled under a season key that has since
        // drifted (e.g. an in-season special regrouped): the stored key no
        // longer exists, but the item does, so the flag must survive cleanup.
        await database.SetItemDisabledAsync(staleSeasonId, retainedItemId, disabled: true);

        // A movie is queued under its own id.
        await database.SetItemDisabledAsync(movieId, movieId, disabled: true);
        await database.SetItemDisabledAsync(removedSeasonId, removedItemId, disabled: true);

        await database.CleanItemStateAsync([retainedItemId, movieId]);

        Assert.Equal([retainedItemId], await database.GetDisabledItemIdsAsync(staleSeasonId));
        Assert.Equal([movieId], await database.GetDisabledItemIdsAsync(movieId));
        Assert.Empty(await database.GetDisabledItemIdsAsync(removedSeasonId));
    }

    [Fact]
    public async Task CleanItemStateAsync_SurvivingFlagKeepsWithholdingSegments()
    {
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var itemId = Guid.NewGuid();
        var staleSeasonId = Guid.NewGuid();

        var intro = new Segment(itemId, new TimeRange(0, 30));
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [intro], SegmentSource.Chromaprint);
        await database.SetItemDisabledAsync(staleSeasonId, itemId, disabled: true);

        // The reviewer's drift repro: the item moved to a retained season key
        // while its disable row still carries the dropped key. Cleanup must not
        // resurrect the automatic segments.
        await database.CleanItemStateAsync([itemId]);

        Assert.Empty(await database.GetServableSegmentsAsync(itemId));
    }

    [Fact]
    public async Task RebuildDatabaseAsync_PreservesDisabledItems()
    {
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var seasonId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        await database.SetItemDisabledAsync(seasonId, itemId, disabled: true);

        await database.RebuildDatabaseAsync();

        Assert.Equal([itemId], await database.GetDisabledItemIdsAsync(seasonId));
    }
}
