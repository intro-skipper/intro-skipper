// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.SegmentChanges;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for the mirror-composed projection adapter: foreign-row deletes and the
/// image sync both flow through the shared store fake, so the assertions see exactly
/// what Jellyfin would.
/// </summary>
public sealed class TestJellyfinSegmentProjectionAdapter
{
    [Fact]
    public async Task Apply_DeletesValidatedForeignRowAndSyncsImage()
    {
        var itemId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var (adapter, store, database) = Create(SegmentChangeHarness.MirroredDto(itemId, foreignId, MediaSegmentType.Intro, 10, 20));
        var own = await database.SeedUserSegmentAsync(itemId, AnalysisMode.Credits, 30, 40);

        var outcome = await adapter.ApplyAsync(itemId, [Delete(foreignId)], CancellationToken.None);

        Assert.True(outcome);
        Assert.Contains((itemId, foreignId), store.DeletedSegments);
        var (replacedItem, replaced) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItem);
        Assert.Equal(own.Id, Assert.Single(replaced).Id);
    }

    // The operation was validated as Intro 10..20; the row was rewritten under its
    // stable id since then (type or boundaries). The predicate travels inside the
    // delete, so the row is dropped, not deleted, and the operation is not retried.
    [Theory]
    [InlineData(MediaSegmentType.Outro, 10, 20)]
    [InlineData(MediaSegmentType.Intro, 100, 200)]
    public async Task Apply_DropsRewrittenRowOperationWithoutDeleting(MediaSegmentType currentType, long currentStart, long currentEnd)
    {
        var itemId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var (adapter, store, _) = Create(SegmentChangeHarness.MirroredDto(itemId, foreignId, currentType, currentStart, currentEnd));

        var outcome = await adapter.ApplyAsync(itemId, [Delete(foreignId)], CancellationToken.None);

        Assert.True(outcome);
        Assert.Empty(store.DeletedSegments);
    }

    [Fact]
    public async Task Apply_MissingForeignRowIsIdempotentSuccess()
    {
        var itemId = Guid.NewGuid();
        var (adapter, store, _) = Create();

        var outcome = await adapter.ApplyAsync(itemId, [Delete(Guid.NewGuid())], CancellationToken.None);

        Assert.True(outcome);
        Assert.Empty(store.DeletedSegments);
    }

    [Fact]
    public async Task ResolveExternalTarget_ReportsActualOwnerAcrossItems()
    {
        var otherItemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var (adapter, _, _) = Create(SegmentChangeHarness.MirroredDto(otherItemId, segmentId, MediaSegmentType.Intro, 10, 20));

        var target = await adapter.ResolveExternalTargetAsync(Guid.NewGuid(), segmentId, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(otherItemId, target.ItemId);
        Assert.Null(await adapter.ResolveExternalTargetAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>A journaled delete validated as Intro 10..20.</summary>
    private static DbProjectionExternalOperation Delete(Guid externalSegmentId)
        => new() { ExternalSegmentId = externalSegmentId, ExpectedType = MediaSegmentType.Intro, StartTicks = 10, EndTicks = 20 };

    private static (JellyfinSegmentProjectionAdapter Adapter, FakeJellyfinSegmentStore Store, IntroSkipperDatabase Database) Create(params MediaSegmentDto[] existingSegments)
    {
        var store = new FakeJellyfinSegmentStore { ExistingSegments = [.. existingSegments] };
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var adapter = new JellyfinSegmentProjectionAdapter(
            store,
            DatabaseTestHelpers.CreateMirror(store, database),
            NullLogger<JellyfinSegmentProjectionAdapter>.Instance);
        return (adapter, store, database);
    }
}
