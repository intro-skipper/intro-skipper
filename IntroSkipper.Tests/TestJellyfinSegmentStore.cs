// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

        // The new segment's explicit id collides with the foreign row's primary key. The
        // collision is caught after the in-transaction delete already ran, so the delete
        // must be rolled back.
        await Assert.ThrowsAsync<SegmentIdConflictException>(() => store.ReplaceSegmentsAsync(
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

        await Assert.ThrowsAsync<SegmentIdConflictException>(() => store.ReplaceSegmentsAsync(
            itemId,
            [CreateDto(MediaSegmentType.Intro, 10, 20, foreign.Id)],
            CancellationToken.None));

        // Without the store's explicit rollback the failed transaction would leak the
        // process-wide write lock and this follow-up write would hang forever.
        await store.ReplaceSegmentsAsync(itemId, [CreateDto(MediaSegmentType.Intro, 10, 20)], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, (await GetAllAsync(db)).Count);
    }

    /// <summary>
    /// Single-type replace, the shape the editor's create path uses. Unlike the multi-type
    /// test, this seeds a second item carrying the same type, so it pins that the replace
    /// scope is the item as well as the type.
    /// </summary>
    [Fact]
    public async Task ReplaceEditableTypesAsync_SingleType_ReplacesAllProvidersRowsOfThatTypeOnThatItemOnly()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        var foreignOutro = CreateEntity(itemId, MediaSegmentType.Outro, 0, 100, ForeignProviderId);
        var otherItemIntro = CreateEntity(otherItemId, MediaSegmentType.Intro, 0, 100, ForeignProviderId);
        await SeedAsync(
            db,
            CreateEntity(itemId, MediaSegmentType.Intro, 0, 5, ForeignProviderId),
            CreateEntity(itemId, MediaSegmentType.Intro, 5, 9, JellyfinSegmentStore.ProviderId),
            foreignOutro,
            otherItemIntro);

        await store.ReplaceEditableTypesAsync(
            itemId,
            [CreateDto(MediaSegmentType.Intro, 10, 20)],
            [MediaSegmentType.Intro],
            CancellationToken.None);

        var rows = await GetAllAsync(db);
        Assert.Equal(3, rows.Count);
        var intro = Assert.Single(rows, row => row.ItemId == itemId && row.Type == MediaSegmentType.Intro);
        Assert.Equal(JellyfinSegmentStore.ProviderId, intro.SegmentProviderId);
        Assert.Equal(10, intro.StartTicks);
        Assert.Equal(20, intro.EndTicks);
        Assert.Single(rows, row => row.Id == foreignOutro.Id);
        Assert.Single(rows, row => row.Id == otherItemIntro.Id);
    }

    [Fact]
    public async Task CreateCommercialIfAbsentAsync_SkipsCreate_WhenIdenticalExists()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        await SeedAsync(db, CreateEntity(itemId, MediaSegmentType.Commercial, 10, 20, ForeignProviderId));

        await store.CreateCommercialIfAbsentAsync(itemId, CreateDto(MediaSegmentType.Commercial, 10, 20), CancellationToken.None);

        Assert.Single(await GetAllAsync(db));
    }

    [Fact]
    public async Task CreateCommercialIfAbsentAsync_Creates_WhenNoIdenticalExists()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        await SeedAsync(db, CreateEntity(itemId, MediaSegmentType.Commercial, 10, 20, ForeignProviderId));

        await store.CreateCommercialIfAbsentAsync(itemId, CreateDto(MediaSegmentType.Commercial, 30, 40), CancellationToken.None);

        var rows = await GetAllAsync(db);
        Assert.Equal(2, rows.Count);
        var created = Assert.Single(rows, row => row.SegmentProviderId == JellyfinSegmentStore.ProviderId);
        Assert.Equal(30, created.StartTicks);
        Assert.Equal(40, created.EndTicks);
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

    [Fact]
    public async Task ReplaceEditableTypesAsync_ReplacesListedTypesAcrossProviders_AndKeepsOthers()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var foreignRecap = CreateEntity(itemId, MediaSegmentType.Recap, 0, 5, ForeignProviderId);
        await SeedAsync(
            db,
            CreateEntity(itemId, MediaSegmentType.Intro, 0, 5, ForeignProviderId),
            CreateEntity(itemId, MediaSegmentType.Commercial, 50, 60, ForeignProviderId),
            CreateEntity(itemId, MediaSegmentType.Commercial, 80, 90, JellyfinSegmentStore.ProviderId),
            foreignRecap);

        await store.ReplaceEditableTypesAsync(
            itemId,
            [
                CreateDto(MediaSegmentType.Intro, 10, 20),
                CreateDto(MediaSegmentType.Commercial, 100, 110),
                CreateDto(MediaSegmentType.Commercial, 200, 210),
            ],
            [MediaSegmentType.Intro, MediaSegmentType.Commercial],
            CancellationToken.None);

        var rows = await GetAllAsync(db);
        Assert.Equal(4, rows.Count);
        Assert.Single(rows, row => row.Id == foreignRecap.Id);
        var intro = Assert.Single(rows, row => row.Type == MediaSegmentType.Intro);
        Assert.Equal(JellyfinSegmentStore.ProviderId, intro.SegmentProviderId);
        Assert.Equal(10, intro.StartTicks);
        var commercials = rows.Where(row => row.Type == MediaSegmentType.Commercial).ToList();
        Assert.Equal(2, commercials.Count);
        Assert.All(commercials, row => Assert.Equal(JellyfinSegmentStore.ProviderId, row.SegmentProviderId));
    }

    [Fact]
    public async Task ReplaceEditableTypesAsync_EmptyInput_DeletesListedTypesOnly()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var keptOutro = CreateEntity(itemId, MediaSegmentType.Outro, 0, 5, ForeignProviderId);
        await SeedAsync(
            db,
            CreateEntity(itemId, MediaSegmentType.Intro, 0, 5, JellyfinSegmentStore.ProviderId),
            CreateEntity(itemId, MediaSegmentType.Intro, 6, 9, ForeignProviderId),
            keptOutro);

        await store.ReplaceEditableTypesAsync(itemId, [], [MediaSegmentType.Intro], CancellationToken.None);

        var row = Assert.Single(await GetAllAsync(db));
        Assert.Equal(keptOutro.Id, row.Id);
    }

    [Fact]
    public async Task ReplaceEditableTypesAsync_Throws_WhenSegmentTypeIsNotListed()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceEditableTypesAsync(
            Guid.NewGuid(),
            [CreateDto(MediaSegmentType.Outro, 10, 20)],
            [MediaSegmentType.Intro],
            CancellationToken.None));
    }

    [Fact]
    public async Task GetItemSegmentsAsync_ReturnsProviderIds_OrderedByStart()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        await SeedAsync(
            db,
            CreateEntity(itemId, MediaSegmentType.Outro, 300, 400, ForeignProviderId),
            CreateEntity(itemId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId),
            CreateEntity(Guid.NewGuid(), MediaSegmentType.Intro, 0, 5, ForeignProviderId));

        var snapshots = await store.GetItemSegmentsAsync(itemId, CancellationToken.None);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(MediaSegmentType.Intro, snapshots[0].Type);
        Assert.Equal(JellyfinSegmentStore.ProviderId, snapshots[0].ProviderId);
        Assert.Equal(MediaSegmentType.Outro, snapshots[1].Type);
        Assert.Equal(ForeignProviderId, snapshots[1].ProviderId);
    }

    [Fact]
    public async Task GetItemSegmentCountsAsync_SplitsOwnAndOtherPerItem()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        await SeedAsync(
            db,
            CreateEntity(itemA, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId),
            CreateEntity(itemA, MediaSegmentType.Outro, 30, 40, ForeignProviderId),
            CreateEntity(itemA, MediaSegmentType.Commercial, 50, 60, ForeignProviderId),
            CreateEntity(itemB, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId));

        var counts = await store.GetItemSegmentCountsAsync(CancellationToken.None);

        Assert.Equal(2, counts.Count);
        var entryA = Assert.Single(counts, entry => entry.ItemId == itemA);
        Assert.Equal(1, entryA.OwnCount);
        Assert.Equal(2, entryA.OtherCount);
        var entryB = Assert.Single(counts, entry => entry.ItemId == itemB);
        Assert.Equal(1, entryB.OwnCount);
        Assert.Equal(0, entryB.OtherCount);
    }

    [Fact]
    public async Task ReplaceEditableTypesAsync_ThrowsTypedConflict_WhenSuppliedIdBelongsToAnotherItem()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var otherItemsRow = CreateEntity(itemB, MediaSegmentType.Intro, 0, 100, ForeignProviderId);
        var itemARow = CreateEntity(itemA, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId);
        await SeedAsync(db, otherItemsRow, itemARow);

        // The supplied id survives the scoped delete (it lives on another item), so the
        // editor path must fail with the typed conflict, not a raw constraint violation.
        var ex = await Assert.ThrowsAsync<SegmentIdConflictException>(() => store.ReplaceEditableTypesAsync(
            itemA,
            [CreateDto(MediaSegmentType.Intro, 50, 60, otherItemsRow.Id)],
            [MediaSegmentType.Intro],
            CancellationToken.None));

        Assert.Equal(otherItemsRow.Id, ex.SegmentId);

        // The transaction rolled back: item A's deleted row is back, item B untouched.
        var rows = await GetAllAsync(db);
        Assert.Equal(2, rows.Count);
        var survivor = Assert.Single(rows, row => row.Id == itemARow.Id);
        Assert.Equal(10, survivor.StartTicks);
        Assert.Single(rows, row => row.Id == otherItemsRow.Id);
    }

    [Fact]
    public async Task ReplaceEditableTypesAsync_AllowsReusingIdsFromTheReplacedScope()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var existing = CreateEntity(itemId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId);
        await SeedAsync(db, existing);

        // A GET-then-PUT round-trip legitimately reuses the ids of rows the replace
        // deletes; the conflict check runs after the scoped delete so this stays valid.
        await store.ReplaceEditableTypesAsync(
            itemId,
            [CreateDto(MediaSegmentType.Intro, 50, 60, existing.Id)],
            [MediaSegmentType.Intro],
            CancellationToken.None);

        var row = Assert.Single(await GetAllAsync(db));
        Assert.Equal(existing.Id, row.Id);
        Assert.Equal(50, row.StartTicks);
        Assert.Equal(60, row.EndTicks);
    }

    [Fact]
    public async Task CreateCommercialIfAbsentAsync_ThrowsTypedConflict_WhenSuppliedIdBelongsToAnotherRow()
    {
        using var db = new TempJellyfinDb();
        var store = CreateStore(db);
        var itemId = Guid.NewGuid();
        var existing = CreateEntity(itemId, MediaSegmentType.Intro, 0, 100, ForeignProviderId);
        await SeedAsync(db, existing);

        var ex = await Assert.ThrowsAsync<SegmentIdConflictException>(() => store.CreateCommercialIfAbsentAsync(
            itemId,
            CreateDto(MediaSegmentType.Commercial, 50, 60, existing.Id),
            CancellationToken.None));

        Assert.Equal(existing.Id, ex.SegmentId);
        Assert.Single(await GetAllAsync(db));
    }

    [Fact]
    public async Task CreateCommercialIfAbsentAsync_TranslatesRacedIdClaim_IntoTypedConflict()
    {
        var itemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        var conflictingId = Guid.NewGuid();

        // Claims the supplied id from a second connection after the store's pre-insert
        // check has passed, immediately before the store's own SaveChanges runs — the
        // exact gap the non-transactional check-then-insert leaves open.
        TempJellyfinDb? db = null;
        var interceptor = new OneShotBeforeSaveInterceptor(
            () => SeedAsync(db!, CreateEntity(otherItemId, MediaSegmentType.Intro, 0, 100, ForeignProviderId, conflictingId)));
        using var tempDb = new TempJellyfinDb(null, interceptor);
        db = tempDb;
        var store = CreateStore(tempDb);

        var ex = await Assert.ThrowsAsync<SegmentIdConflictException>(() => store.CreateCommercialIfAbsentAsync(
            itemId,
            CreateDto(MediaSegmentType.Commercial, 50, 60, conflictingId),
            CancellationToken.None));

        Assert.Equal(conflictingId, ex.SegmentId);

        // Only the racing row exists; the store's failed insert was never committed.
        var row = Assert.Single(await GetAllAsync(tempDb));
        Assert.Equal(otherItemId, row.ItemId);
        Assert.Equal(conflictingId, row.Id);
    }

    [Fact]
    public async Task CreateCommercialIfAbsentAsync_SurfacesOriginalFailure_WhenConflictRecheckAlsoFails()
    {
        var interceptor = new UnavailableStoreInterceptor();
        using var db = new TempJellyfinDb(null, interceptor);
        var store = CreateStore(db);

        // The store goes down during the insert, so the post-failure id re-check hits the
        // same outage. The re-check is a diagnostic, not the failure: it must not replace
        // the actionable root cause with its own secondary error.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateCommercialIfAbsentAsync(
            Guid.NewGuid(),
            CreateDto(MediaSegmentType.Commercial, 50, 60, Guid.NewGuid()),
            CancellationToken.None));

        Assert.Equal("store went down mid-insert", ex.Message);
        Assert.True(interceptor.RecheckAttempted);
    }

    /// <summary>
    /// Fails the first media-segment SaveChanges and then makes every later read on the
    /// same context throw, so a test can drive the case where a store outage breaks both
    /// the write and the diagnostic re-check that follows it.
    /// </summary>
    private sealed class UnavailableStoreInterceptor : ISaveChangesInterceptor, IDbCommandInterceptor
    {
        private volatile bool _down;

        internal bool RecheckAttempted { get; private set; }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is JellyfinDbContext context
                && context.ChangeTracker.Entries<MediaSegment>().Any(entry => entry.State == EntityState.Added))
            {
                _down = true;
                throw new DbUpdateException("store went down mid-insert");
            }

            return ValueTask.FromResult(result);
        }

        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
            => ThrowIfDown(result);

        public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
            => ThrowIfDown(result);

        private ValueTask<InterceptionResult<T>> ThrowIfDown<T>(InterceptionResult<T> result)
        {
            if (!_down)
            {
                return ValueTask.FromResult(result);
            }

            RecheckAttempted = true;
            throw new InvalidOperationException("store is unavailable");
        }
    }

    /// <summary>
    /// Runs a callback once, immediately before the first SaveChanges that inserts a
    /// media segment, so a test can interleave a concurrent write into the store's
    /// check-then-save gap. The callback's own SaveChanges is not intercepted again.
    /// </summary>
    private sealed class OneShotBeforeSaveInterceptor(Func<Task> beforeFirstSave) : ISaveChangesInterceptor
    {
        private int _fired;

        public async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is JellyfinDbContext context
                && context.ChangeTracker.Entries<MediaSegment>().Any(entry => entry.State == EntityState.Added)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await beforeFirstSave();
            }

            return result;
        }
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
