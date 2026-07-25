// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestMediaSegmentEditorService
{
    [Fact]
    public async Task CreateOrReplaceSegmentAsync_RoutesNonCommercialToReplaceType()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store);
        var segment = CreateSegment(MediaSegmentType.Intro, 10, 20);

        await service.CreateOrReplaceSegmentAsync(CreateMovie(itemId), Guid.NewGuid(), segment, CancellationToken.None);

        // Scoped to the segment's own type, so the replace cannot touch another type.
        var (replacedItemId, replacedSegments, replacedTypes) = Assert.Single(store.ReplacedEditableTypes);
        Assert.Equal(itemId, replacedItemId);
        Assert.Same(segment, Assert.Single(replacedSegments));
        Assert.Equal(MediaSegmentType.Intro, Assert.Single(replacedTypes));
        Assert.Empty(store.CreatedCommercials);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_RoutesCommercialToCreateIfAbsent()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store);
        var segment = CreateSegment(MediaSegmentType.Commercial, 10, 20);

        await service.CreateOrReplaceSegmentAsync(CreateMovie(itemId), Guid.NewGuid(), segment, CancellationToken.None);

        var (createdItemId, createdSegment) = Assert.Single(store.CreatedCommercials);
        Assert.Equal(itemId, createdItemId);
        Assert.Same(segment, createdSegment);
        Assert.Empty(store.ReplacedEditableTypes);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_SerializesConcurrentCallsForSameItem()
    {
        var item = CreateMovie(Guid.NewGuid());
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            WriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = item.Id
        };
        var service = CreateService(store);

        // First call enters the critical section and parks inside the store write while holding the lock.
        var first = service.CreateOrReplaceSegmentAsync(item, Guid.NewGuid(), CreateSegment(MediaSegmentType.Intro, 10, 20), CancellationToken.None);
        await store.WriteEntered.Task;

        // Second call for the same item must block on the per-item lock and therefore must not have
        // reached the store yet.
        var second = service.CreateOrReplaceSegmentAsync(item, Guid.NewGuid(), CreateSegment(MediaSegmentType.Intro, 30, 40), CancellationToken.None);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, store.WriteCallCount);

        store.WriteGate!.SetResult();
        await first;
        await second;

        Assert.Equal(2, store.WriteCallCount);
        Assert.Equal(2, store.ReplacedEditableTypes.Count);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_AllowsConcurrentCallsForDifferentItems()
    {
        var firstItem = CreateMovie(Guid.NewGuid());
        var secondItem = CreateMovie(Guid.NewGuid());
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = firstItem.Id
        };
        var service = CreateService(store);

        var first = service.CreateOrReplaceSegmentAsync(firstItem, Guid.NewGuid(), CreateSegment(MediaSegmentType.Intro, 10, 20), CancellationToken.None);
        var second = service.CreateOrReplaceSegmentAsync(secondItem, Guid.NewGuid(), CreateSegment(MediaSegmentType.Intro, 30, 40), CancellationToken.None);

        Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1))));
        Assert.False(first.IsCompleted);

        store.WriteGate!.SetResult();
        await first;

        Assert.Equal(2, store.WriteCallCount);
    }

    [Fact]
    public async Task DeleteSegmentAsync_StillContactsStore_ButReportsNotDeleted_WhenIdExistsNowhere()
    {
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store);
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();

        var result = await service.DeleteSegmentAsync(
            itemId,
            segmentId,
            AnalysisMode.Introduction,
            CancellationToken.None);

        // The no-op delete is still issued: it is the escape hatch's only contact with the
        // store, so skipping it would hide a store that is failing outright.
        Assert.Equal([(itemId, segmentId)], store.DeletedSegments);

        // Nothing was removed from either store, so the caller must not treat this as a
        // success and re-queue the episode for analysis.
        Assert.False(result.Deleted);
        Assert.False(result.OwnSegmentsChanged);
    }

    [Fact]
    public async Task ReplaceEditorSegmentsAsync_WritesUserRows_AndReplacesMappedTypesInStore()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store, database);
        var segment = CreateSegment(MediaSegmentType.Intro, TimeSpan.FromSeconds(10).Ticks, TimeSpan.FromSeconds(20).Ticks);

        await service.ReplaceEditorSegmentsAsync(CreateMovie(itemId), Guid.NewGuid(), [segment], [AnalysisMode.Introduction], CancellationToken.None);

        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(AnalysisMode.Introduction, row.Type);
        Assert.True(row.IsUserProvided);
        Assert.Equal(10, row.Start);
        Assert.Equal(20, row.End);
        var (storeItemId, storeSegments, storeTypes) = Assert.Single(store.ReplacedEditableTypes);
        Assert.Equal(itemId, storeItemId);
        Assert.Same(segment, Assert.Single(storeSegments));
        Assert.Equal([MediaSegmentType.Intro], storeTypes);
    }

    [Fact]
    public async Task ReplaceEditorSegmentsAsync_RestoresPriorRowsExactly_WhenStoreFails()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Introduction, configHash: "cfg-auto");
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(500, 560)), AnalysisMode.Credits, isUserProvided: true, configHash: "cfg-user");
        var store = new FakeJellyfinSegmentStore { WriteException = new InvalidOperationException("jellyfin down") };
        var service = CreateService(store, database);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplaceEditorSegmentsAsync(
            CreateMovie(itemId),
            Guid.NewGuid(),
            [CreateSegment(MediaSegmentType.Intro, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromSeconds(2).Ticks)],
            [AnalysisMode.Introduction, AnalysisMode.Credits],
            CancellationToken.None));

        var rows = await database.GetSegmentsAsync(itemId);
        Assert.Equal(2, rows.Count);
        var intro = Assert.Single(rows, row => row.Type == AnalysisMode.Introduction);
        Assert.False(intro.IsUserProvided);
        Assert.Equal("cfg-auto", intro.ConfigHash);
        Assert.Equal(10, intro.Start);
        var credits = Assert.Single(rows, row => row.Type == AnalysisMode.Credits);
        Assert.True(credits.IsUserProvided);
        Assert.Equal("cfg-user", credits.ConfigHash);
    }

    [Fact]
    public async Task ReplaceEditorSegmentsAsync_RemovesSeasonEpisode_OnlyForClearedModes()
    {
        var itemId = Guid.NewGuid();
        var seasonKey = Guid.NewGuid();
        var otherEpisodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Introduction);
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(500, 560)), AnalysisMode.Credits);
        await database.SetEpisodeIdsAsync(seasonKey, AnalysisMode.Introduction, [itemId, otherEpisodeId], "hash");
        await database.SetEpisodeIdsAsync(seasonKey, AnalysisMode.Credits, [itemId, otherEpisodeId], "hash");
        var service = CreateService(new FakeJellyfinSegmentStore(), database);

        // Credits gets a replacement; Introduction is cleared.
        await service.ReplaceEditorSegmentsAsync(
            CreateMovie(itemId),
            seasonKey,
            [CreateSegment(MediaSegmentType.Outro, TimeSpan.FromSeconds(600).Ticks, TimeSpan.FromSeconds(660).Ticks)],
            [AnalysisMode.Introduction, AnalysisMode.Credits],
            CancellationToken.None);

        var snapshot = await database.GetSeasonQueueSnapshotAsync(seasonKey, [itemId, otherEpisodeId]);
        Assert.DoesNotContain(itemId, snapshot.EpisodeIdsByMode[AnalysisMode.Introduction]);
        Assert.Contains(otherEpisodeId, snapshot.EpisodeIdsByMode[AnalysisMode.Introduction]);
        Assert.Contains(itemId, snapshot.EpisodeIdsByMode[AnalysisMode.Credits]);
    }

    [Fact]
    public async Task ReplaceEditorSegmentsAsync_SerializesWithCreateOrReplace_ForSameItem()
    {
        var item = CreateMovie(Guid.NewGuid());
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeJellyfinSegmentStore
        {
            WriteEntered = writeEntered,
            WriteGate = writeGate,
            BlockedItemId = item.Id
        };
        var service = CreateService(store);

        var first = service.ReplaceEditorSegmentsAsync(
            item,
            Guid.NewGuid(),
            [CreateSegment(MediaSegmentType.Intro, 10, 20)],
            [AnalysisMode.Introduction],
            CancellationToken.None);
        await writeEntered.Task;

        var second = service.CreateOrReplaceSegmentAsync(item, Guid.NewGuid(), CreateSegment(MediaSegmentType.Outro, 30, 40), CancellationToken.None);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, store.WriteCallCount);

        writeGate.SetResult();
        await first;
        await second;

        Assert.Equal(2, store.WriteCallCount);
    }

    [Fact]
    public async Task ReplaceEditorSegmentsAsync_FinishesSeasonCleanup_WhenCancellationArrivesAfterStoreCommit()
    {
        var itemId = Guid.NewGuid();
        var seasonKey = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Introduction);
        await database.SetEpisodeIdsAsync(seasonKey, AnalysisMode.Introduction, [itemId], "hash");
        using var cts = new CancellationTokenSource();
        var store = new FakeJellyfinSegmentStore { EditableTypesWriteCompleted = cts.Cancel };
        var service = CreateService(store, database);

        await service.ReplaceEditorSegmentsAsync(
            CreateMovie(itemId),
            seasonKey,
            [],
            [AnalysisMode.Introduction],
            cts.Token);

        Assert.True(cts.IsCancellationRequested);
        var snapshot = await database.GetSeasonQueueSnapshotAsync(seasonKey, [itemId]);
        Assert.DoesNotContain(itemId, snapshot.EpisodeIdsByMode[AnalysisMode.Introduction]);
    }

    [Fact]
    public async Task DeleteSegmentAsync_WaitsForReplacementOfSameItem()
    {
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments = [new JellyfinSegmentSnapshot(segmentId, itemId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId)],
            WriteEntered = writeEntered,
            WriteGate = writeGate,
            GateOnlyFirstWrite = true,
            DeleteEntered = deleteEntered,
            BlockedItemId = itemId
        };
        var service = CreateService(store);

        var replacement = service.ReplaceEditorSegmentsAsync(
            CreateMovie(itemId),
            Guid.NewGuid(),
            [CreateSegment(MediaSegmentType.Intro, 10, 20, segmentId)],
            [AnalysisMode.Introduction],
            CancellationToken.None);
        await writeEntered.Task;

        var deletion = service.DeleteSegmentAsync(
            itemId,
            segmentId,
            AnalysisMode.Introduction,
            CancellationToken.None);

        Assert.NotSame(deleteEntered.Task, await Task.WhenAny(deleteEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(250))));
        Assert.Empty(store.DeletedSegments);

        writeGate.SetResult();
        await replacement;
        var result = await deletion;

        Assert.True(result.Deleted);
        Assert.Equal([(itemId, segmentId)], store.DeletedSegments);
    }

    private static MediaSegmentEditorService CreateService(
        FakeJellyfinSegmentStore store,
        IIntroSkipperDatabase? database = null,
        IEnumerable<IMediaSegmentProvider>? providers = null)
        => new(store, database ?? DatabaseTestHelpers.CreateTempSegmentDatabase(), providers ?? [], NullLogger<MediaSegmentEditorService>.Instance);

    [Fact]
    public async Task DeleteSegmentAsync_ForeignProviderRow_DeletesJellyfinRowOnly_AndReportsNoOwnChange()
    {
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(100, 160)), AnalysisMode.Introduction, isUserProvided: true, configHash: "cfg");

        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments = [new JellyfinSegmentSnapshot(segmentId, itemId, MediaSegmentType.Intro, TimeSpan.FromSeconds(95).Ticks, TimeSpan.FromSeconds(165).Ticks, "foreign-provider")],
        };
        var service = CreateService(store, database);

        var result = await service.DeleteSegmentAsync(itemId, segmentId, AnalysisMode.Introduction, CancellationToken.None);

        Assert.True(result.Deleted);
        Assert.Null(result.ActualType);
        Assert.False(result.OwnSegmentsChanged);
        Assert.Equal([(itemId, segmentId)], store.DeletedSegments);

        // Intro Skipper's plugin row is not the foreign row's counterpart and must survive.
        var survivor = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(AnalysisMode.Introduction, survivor.Type);
        Assert.Equal(100, survivor.Start);
        Assert.True(survivor.IsUserProvided);
    }

    [Fact]
    public async Task ReplaceEditorSegmentsAsync_SurfacesStoreFailure_WhenCompensationAlsoFails()
    {
        var itemId = Guid.NewGuid();
        var database = new ReplaceFailingDatabase(
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            failFromCall: 2,
            new IOException("restore failed"));
        var store = new FakeJellyfinSegmentStore { WriteException = new InvalidOperationException("jellyfin down") };
        var service = CreateService(store, database);

        // The plugin replacement (call 1) commits, the Jellyfin write fails, and the
        // compensating replacement (call 2) fails too. The original Jellyfin failure is
        // the actionable root cause and must be the exception that surfaces.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplaceEditorSegmentsAsync(
            CreateMovie(itemId),
            itemId,
            [CreateSegment(MediaSegmentType.Intro, TimeSpan.FromSeconds(10).Ticks, TimeSpan.FromSeconds(20).Ticks)],
            [AnalysisMode.Introduction],
            CancellationToken.None));

        Assert.Equal("jellyfin down", thrown.Message);
        Assert.Equal(2, database.ReplaceCallCount);
    }

    [Fact]
    public async Task DeleteSegmentAsync_SurfacesStoreFailure_WhenRestoreAlsoFails()
    {
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var inner = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await inner.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Introduction, isUserProvided: true);
        var database = new ReplaceFailingDatabase(inner, failFromCall: 1, new IOException("restore failed"));
        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments = [new JellyfinSegmentSnapshot(segmentId, itemId, MediaSegmentType.Intro, TimeSpan.FromSeconds(10).Ticks, TimeSpan.FromSeconds(20).Ticks, JellyfinSegmentStore.ProviderId)],
            DeleteSegmentException = new InvalidOperationException("jellyfin down"),
        };
        var service = CreateService(store, database);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteSegmentAsync(itemId, segmentId, AnalysisMode.Introduction, CancellationToken.None));

        Assert.Equal("jellyfin down", thrown.Message);
        Assert.Equal(1, database.ReplaceCallCount);
    }

    /// <summary>
    /// Delegates to a real database facade but fails
    /// <see cref="IIntroSkipperDatabase.ReplaceItemSegmentsAsync"/> from a configured call
    /// onward, so tests can drive the editor service's compensation into a failure.
    /// </summary>
    private sealed class ReplaceFailingDatabase(IIntroSkipperDatabase inner, int failFromCall, Exception failure) : IIntroSkipperDatabase
    {
        public int ReplaceCallCount { get; private set; }

        public Task InitializeAsync() => inner.InitializeAsync();

        public Task UpdateTimestampAsync(Segment segment, AnalysisMode mode, bool isUserProvided = false, string configHash = "", CancellationToken cancellationToken = default)
            => inner.UpdateTimestampAsync(segment, mode, isUserProvided, configHash, cancellationToken);

        public Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(Guid id, CancellationToken cancellationToken = default)
            => inner.GetTimestampsAsync(id, cancellationToken);

        public Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid id, CancellationToken cancellationToken = default)
            => inner.GetSegmentsAsync(id, cancellationToken);

        public Task DeleteItemSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default)
            => inner.DeleteItemSegmentsAsync(itemId, cancellationToken);

        public Task<IReadOnlyList<DbSegment>> DeleteTimestampAsync(Guid itemId, AnalysisMode mode, Segment? segment = null, CancellationToken cancellationToken = default)
            => inner.DeleteTimestampAsync(itemId, mode, segment, cancellationToken);

        public Task<IReadOnlyList<DbSegment>> ReplaceItemSegmentsAsync(Guid itemId, IReadOnlyCollection<AnalysisMode> modes, IReadOnlyCollection<DbSegment> segments, CancellationToken cancellationToken = default)
        {
            ReplaceCallCount++;
            if (ReplaceCallCount >= failFromCall)
            {
                throw failure;
            }

            return inner.ReplaceItemSegmentsAsync(itemId, modes, segments, cancellationToken);
        }

        public Task DeleteSegmentsByModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default)
            => inner.DeleteSegmentsByModeAsync(mode, cancellationToken);

        public Task<int> DeleteSegmentsForItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
            => inner.DeleteSegmentsForItemsAsync(itemIds, cancellationToken);

        public Task ClearSeasonAnalysisAsync(Guid seasonId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
            => inner.ClearSeasonAnalysisAsync(seasonId, itemIds, cancellationToken);

        public Task<int> RemoveItemsFromAnalysisAsync(IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> itemIdsBySeason, CancellationToken cancellationToken = default)
            => inner.RemoveItemsFromAnalysisAsync(itemIdsBySeason, cancellationToken);

        public Task<IReadOnlyCollection<Guid>> GetStaleTimestampEpisodeIdsAsync(IEnumerable<Guid> enabledEpisodeIds, CancellationToken cancellationToken = default)
            => inner.GetStaleTimestampEpisodeIdsAsync(enabledEpisodeIds, cancellationToken);

        public Task CleanStaleAutomaticSegmentsAsync(IEnumerable<Guid> itemIds, AnalysisMode mode, string configHash, CancellationToken cancellationToken = default)
            => inner.CleanStaleAutomaticSegmentsAsync(itemIds, mode, configHash, cancellationToken);

        public Task SetAnalyzerActionAsync(Guid seasonId, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default)
            => inner.SetAnalyzerActionAsync(seasonId, analyzerActions, cancellationToken);

        public Task SetEpisodeIdsAsync(Guid seasonId, AnalysisMode mode, IEnumerable<Guid> episodeIds, string configHash = "", CancellationToken cancellationToken = default)
            => inner.SetEpisodeIdsAsync(seasonId, mode, episodeIds, configHash, cancellationToken);

        public Task RemoveEpisodeIdAsync(Guid seasonId, AnalysisMode mode, Guid episodeId, CancellationToken cancellationToken = default)
            => inner.RemoveEpisodeIdAsync(seasonId, mode, episodeId, cancellationToken);

        public Task<IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>> GetSettleReanalysisStatesAsync(Guid seasonId, CancellationToken cancellationToken = default)
            => inner.GetSettleReanalysisStatesAsync(seasonId, cancellationToken);

        public Task RecordSettleReanalysisAsync(Guid seasonId, IReadOnlyCollection<AnalysisMode> modes, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
            => inner.RecordSettleReanalysisAsync(seasonId, modes, episodeIds, cancellationToken);

        public Task ResetSeasonForReanalysisAsync(Guid seasonId, IEnumerable<Guid> episodeIds, IReadOnlyCollection<AnalysisMode> modes, CancellationToken cancellationToken = default)
            => inner.ResetSeasonForReanalysisAsync(seasonId, episodeIds, modes, cancellationToken);

        public Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(Guid seasonId, CancellationToken cancellationToken = default)
            => inner.GetAllAnalyzerActionsAsync(seasonId, cancellationToken);

        public Task<AnalyzerAction> GetAnalyzerActionAsync(Guid seasonId, AnalysisMode mode, CancellationToken cancellationToken = default)
            => inner.GetAnalyzerActionAsync(seasonId, mode, cancellationToken);

        public Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
            => inner.GetSeasonQueueSnapshotAsync(seasonId, episodeIds, cancellationToken);

        public Task CleanSeasonStateAsync(IEnumerable<Guid> seasonIds, CancellationToken cancellationToken = default)
            => inner.CleanSeasonStateAsync(seasonIds, cancellationToken);

        public Task RebuildDatabaseAsync(bool forceCleanOnBackupFailure = false, CancellationToken cancellationToken = default)
            => inner.RebuildDatabaseAsync(forceCleanOnBackupFailure, cancellationToken);
    }

    private static Movie CreateMovie(Guid id) => EntrypointTestHelpers.CreateMovie(id);

    private static MediaSegmentDto CreateSegment(MediaSegmentType type, long startTicks, long endTicks, Guid id = default, Guid itemId = default)
        => new()
        {
            Id = id,
            ItemId = itemId,
            Type = type,
            StartTicks = startTicks,
            EndTicks = endTicks
        };
}
