// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Integration tests for <see cref="JellyfinSegmentStore"/> over a real Jellyfin context
/// on temp-file SQLite: provider scoping, atomicity, and parity with the server's
/// provider-id derivation.
/// </summary>
public sealed class TestJellyfinSegmentStore
{
    private const string ForeignProviderId = "other-provider";

    [Fact]
    public void ProviderId_MatchesJellyfinDerivation()
    {
        // Mirrors MediaSegmentManager.GetProviderId: MD5 (UTF-16) of the lower-cased
        // provider name, formatted as a dashless guid. Computed here from raw primitives
        // so an accidental change in the store's derivation fails this test.
        var expected = new Guid(MD5.HashData(Encoding.Unicode.GetBytes("intro skipper")))
            .ToString("N", CultureInfo.InvariantCulture);

        Assert.Equal(expected, JellyfinSegmentStore.ProviderId);
    }

    [Fact]
    public async Task ReplaceSegmentsAsync_InsertsOwnRows_AndPreservesOtherProviders()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var foreign = CreateEntity(itemId, MediaSegmentType.Intro, 0, 100, ForeignProviderId);
        await SeedAsync(db, foreign);

        await store.ReplaceSegmentsAsync(
            itemId,
            [CreateDto(MediaSegmentType.Intro, 10, 20), CreateDto(MediaSegmentType.Outro, 30, 40)],
            CancellationToken.None);

        var rows = await GetAllAsync(db);
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, row => row.Id == foreign.Id && row.SegmentProviderId == ForeignProviderId);
        var ownRows = rows.Where(row => row.SegmentProviderId == JellyfinSegmentStore.ProviderId).ToList();
        Assert.Equal(2, ownRows.Count);
        Assert.All(ownRows, row => Assert.Equal(itemId, row.ItemId));
        Assert.All(ownRows, row => Assert.NotEqual(Guid.Empty, row.Id));
        Assert.Single(ownRows, row => row.Type == MediaSegmentType.Intro && row.StartTicks == 10 && row.EndTicks == 20);
        Assert.Single(ownRows, row => row.Type == MediaSegmentType.Outro && row.StartTicks == 30 && row.EndTicks == 40);
    }

    [Fact]
    public async Task ReplaceSegmentsAsync_ReplacesPreviousOwnRows()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();

        await store.ReplaceSegmentsAsync(itemId, [CreateDto(MediaSegmentType.Intro, 10, 20)], CancellationToken.None);
        await store.ReplaceSegmentsAsync(itemId, [CreateDto(MediaSegmentType.Intro, 50, 60)], CancellationToken.None);

        var row = Assert.Single(await GetAllAsync(db));
        Assert.Equal(50, row.StartTicks);
        Assert.Equal(60, row.EndTicks);
    }

    [Fact]
    public async Task ReplaceSegmentsAsync_EmptyInput_DeletesOwnRowsOnly()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        var foreign = CreateEntity(itemId, MediaSegmentType.Intro, 0, 100, ForeignProviderId);
        var ownOtherItem = CreateEntity(otherItemId, MediaSegmentType.Intro, 0, 100, JellyfinSegmentStore.ProviderId);
        await SeedAsync(
            db,
            CreateEntity(itemId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId),
            CreateEntity(itemId, MediaSegmentType.Outro, 30, 40, JellyfinSegmentStore.ProviderId),
            foreign,
            ownOtherItem);

        await store.ReplaceSegmentsAsync(itemId, [], CancellationToken.None);

        var rows = await GetAllAsync(db);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, row => row.Id == foreign.Id);
        Assert.Single(rows, row => row.Id == ownOtherItem.Id);
    }

    [Fact]
    public async Task ReplaceSegmentsAsync_RollsBackDelete_WhenInsertFails()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var existingOwn = CreateEntity(itemId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId);
        var foreign = CreateEntity(itemId, MediaSegmentType.Outro, 0, 100, ForeignProviderId);
        await SeedAsync(db, existingOwn, foreign);

        // The new segment's explicit id collides with the foreign row's primary key,
        // so the insert fails after the in-transaction delete already ran: the delete
        // must be rolled back.
        await Assert.ThrowsAsync<DbUpdateException>(() => store.ReplaceSegmentsAsync(
            itemId,
            [CreateDto(MediaSegmentType.Intro, 50, 60, foreign.Id)],
            CancellationToken.None));

        var rows = await GetAllAsync(db);
        Assert.Equal(2, rows.Count);
        var survivor = Assert.Single(rows, row => row.Id == existingOwn.Id);
        Assert.Equal(10, survivor.StartTicks);
        Assert.Single(rows, row => row.Id == foreign.Id);
    }

    [Fact]
    public async Task FailedTransaction_ReleasesPessimisticWriteLock()
    {
        using var db = new TempJellyfinDb(new PessimisticLockBehavior(
            NullLogger<PessimisticLockBehavior>.Instance,
            NullLoggerFactory.Instance));
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var foreign = CreateEntity(itemId, MediaSegmentType.Outro, 0, 100, ForeignProviderId);
        await SeedAsync(db, foreign);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.ReplaceSegmentsAsync(
            itemId,
            [CreateDto(MediaSegmentType.Intro, 10, 20, foreign.Id)],
            CancellationToken.None));

        // Without the store's explicit rollback the failed transaction would leak the
        // process-wide write lock and this follow-up write would hang forever.
        await store.ReplaceSegmentsAsync(itemId, [CreateDto(MediaSegmentType.Intro, 10, 20)], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, (await GetAllAsync(db)).Count);
    }

    [Fact]
    public async Task ReplaceSegmentsAsync_WritesJellyfinRowId_EqualToDtoId()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var sharedId = Guid.NewGuid();

        // The DTO carries the plugin row's id; the Jellyfin row must reuse it verbatim so
        // both databases address the same segment by the same Guid.
        await store.ReplaceSegmentsAsync(itemId, [CreateDto(MediaSegmentType.Intro, 10, 20, sharedId)], CancellationToken.None);

        var row = Assert.Single(await GetAllAsync(db));
        Assert.Equal(sharedId, row.Id);
        Assert.Equal(itemId, row.ItemId);
        Assert.Equal(JellyfinSegmentStore.ProviderId, row.SegmentProviderId);
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsAllFields_AcrossProviders()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        await SeedAsync(db, CreateEntity(itemId, MediaSegmentType.Intro, 10, 20, ForeignProviderId, segmentId));

        var result = await store.GetSegmentAsync(itemId, segmentId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(segmentId, result!.Id);
        Assert.Equal(itemId, result.ItemId);
        Assert.Equal(MediaSegmentType.Intro, result.Type);
        Assert.Equal(10, result.StartTicks);
        Assert.Equal(20, result.EndTicks);

        Assert.Null(await store.GetSegmentAsync(Guid.NewGuid(), segmentId, CancellationToken.None));
        Assert.Null(await store.GetSegmentAsync(itemId, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task DeleteSegmentAsync_DeletesAnyProvidersRow()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var foreign = CreateEntity(itemId, MediaSegmentType.Intro, 10, 20, ForeignProviderId);
        var own = CreateEntity(itemId, MediaSegmentType.Outro, 30, 40, JellyfinSegmentStore.ProviderId);
        await SeedAsync(db, foreign, own);

        await store.DeleteSegmentAsync(itemId, foreign.Id, CancellationToken.None);

        var row = Assert.Single(await GetAllAsync(db));
        Assert.Equal(own.Id, row.Id);
    }

    [Fact]
    public async Task DeleteSegmentAsync_IgnoresRowsOfOtherItems()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var rowA = CreateEntity(itemA, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId);
        await SeedAsync(db, rowA);

        // A mismatched item id must not delete another item's segment.
        await store.DeleteSegmentAsync(itemB, rowA.Id, CancellationToken.None);
        Assert.Single(await GetAllAsync(db));

        // An unknown segment id is a no-op rather than an error.
        await store.DeleteSegmentAsync(itemA, Guid.NewGuid(), CancellationToken.None);
        Assert.Single(await GetAllAsync(db));

        await store.DeleteSegmentAsync(itemA, rowA.Id, CancellationToken.None);
        Assert.Empty(await GetAllAsync(db));
    }

    [Fact]
    public async Task DeleteOwnSegmentsAsync_DeletesOnlyOwnRows_ForGivenItems()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var itemC = Guid.NewGuid();
        var foreignA = CreateEntity(itemA, MediaSegmentType.Outro, 0, 100, ForeignProviderId);
        var ownC = CreateEntity(itemC, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId);
        await SeedAsync(
            db,
            CreateEntity(itemA, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId),
            CreateEntity(itemB, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId),
            foreignA,
            ownC);

        await store.DeleteOwnSegmentsAsync([itemA, itemB, Guid.Empty], CancellationToken.None);

        var rows = await GetAllAsync(db);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, row => row.Id == foreignA.Id);
        Assert.Single(rows, row => row.Id == ownC.Id);
    }

    [Fact]
    public async Task DeleteOwnSegmentsAsync_HandlesMoreThanOneChunkOfIds()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
        await SeedAsync(
            db,
            CreateEntity(ids[0], MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId),
            CreateEntity(ids[^1], MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId));

        await store.DeleteOwnSegmentsAsync(ids, CancellationToken.None);

        Assert.Empty(await GetAllAsync(db));
    }

    [Fact]
    public async Task ReplaceSegmentsAsync_Throws_WhenEndBeforeStart_WithoutTouchingDatabase()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var existing = CreateEntity(itemId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId);
        await SeedAsync(db, existing);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReplaceSegmentsAsync(
            itemId,
            [CreateDto(MediaSegmentType.Intro, 20, 10)],
            CancellationToken.None));

        var row = Assert.Single(await GetAllAsync(db));
        Assert.Equal(existing.Id, row.Id);
    }

    private static JellyfinSegmentStore CreateStore(TempJellyfinDb db)
        => new(db.Factory, NullLogger<JellyfinSegmentStore>.Instance);

    private static MediaSegment CreateEntity(
        Guid itemId,
        MediaSegmentType type,
        long startTicks,
        long endTicks,
        string providerId,
        Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            ItemId = itemId,
            Type = type,
            StartTicks = startTicks,
            EndTicks = endTicks,
            SegmentProviderId = providerId
        };

    private static MediaSegmentDto CreateDto(MediaSegmentType type, long startTicks, long endTicks, Guid id = default)
        => new()
        {
            Id = id,
            Type = type,
            StartTicks = startTicks,
            EndTicks = endTicks
        };

    private static async Task SeedAsync(TempJellyfinDb db, params MediaSegment[] rows)
    {
        var context = db.Factory.CreateDbContext();
        await using (context)
        {
            context.MediaSegments.AddRange(rows);
            await context.SaveChangesAsync();
        }
    }

    private static async Task<List<MediaSegment>> GetAllAsync(TempJellyfinDb db)
    {
        var context = db.Factory.CreateDbContext();
        await using (context)
        {
            return await context.MediaSegments.AsNoTracking().OrderBy(row => row.StartTicks).ToListAsync();
        }
    }
}
