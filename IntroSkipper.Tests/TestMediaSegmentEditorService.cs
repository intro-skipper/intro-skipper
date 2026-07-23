// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
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

        await service.CreateOrReplaceSegmentAsync(CreateMovie(itemId), segment, CancellationToken.None);

        var (replacedItemId, replacedSegment) = Assert.Single(store.ReplacedTypes);
        Assert.Equal(itemId, replacedItemId);
        Assert.Same(segment, replacedSegment);
        Assert.Empty(store.CreatedCommercials);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_RoutesCommercialToCreateIfAbsent()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store);
        var segment = CreateSegment(MediaSegmentType.Commercial, 10, 20);

        await service.CreateOrReplaceSegmentAsync(CreateMovie(itemId), segment, CancellationToken.None);

        var (createdItemId, createdSegment) = Assert.Single(store.CreatedCommercials);
        Assert.Equal(itemId, createdItemId);
        Assert.Same(segment, createdSegment);
        Assert.Empty(store.ReplacedTypes);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_SerializesConcurrentCallsForSameItem()
    {
        var item = CreateMovie(Guid.NewGuid());
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = item.Id
        };
        var service = CreateService(store);

        // First call enters the critical section and parks inside the store write while holding the lock.
        var first = service.CreateOrReplaceSegmentAsync(item, CreateSegment(MediaSegmentType.Intro, 10, 20), CancellationToken.None);

        // Second call for the same item must block on the per-item lock and therefore must not have
        // reached the store yet.
        var second = service.CreateOrReplaceSegmentAsync(item, CreateSegment(MediaSegmentType.Intro, 30, 40), CancellationToken.None);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, store.WriteCallCount);

        store.WriteGate!.SetResult();
        await first;
        await second;

        Assert.Equal(2, store.WriteCallCount);
        Assert.Equal(2, store.ReplacedTypes.Count);
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

        var first = service.CreateOrReplaceSegmentAsync(firstItem, CreateSegment(MediaSegmentType.Intro, 10, 20), CancellationToken.None);
        var second = service.CreateOrReplaceSegmentAsync(secondItem, CreateSegment(MediaSegmentType.Intro, 30, 40), CancellationToken.None);

        Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1))));
        Assert.False(first.IsCompleted);

        store.WriteGate!.SetResult();
        await first;

        Assert.Equal(2, store.WriteCallCount);
    }

    [Fact]
    public async Task DeleteSegmentAsync_DelegatesToStore()
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

        Assert.Equal([(itemId, segmentId)], store.DeletedSegments);
        Assert.True(result.Deleted);
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

        var second = service.CreateOrReplaceSegmentAsync(item, CreateSegment(MediaSegmentType.Outro, 30, 40), CancellationToken.None);

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
            ExistingSegments = [CreateSegment(MediaSegmentType.Intro, 10, 20, segmentId)],
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
        => new(store, database ?? DatabaseTestHelpers.CreateTempSegmentDatabase(), providers ?? []);

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
