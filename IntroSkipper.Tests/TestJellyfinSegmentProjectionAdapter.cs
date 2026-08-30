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
        var (adapter, store, database) = Create(new MediaSegmentDto
        {
            Id = foreignId,
            ItemId = itemId,
            Type = MediaSegmentType.Intro,
            StartTicks = 10,
            EndTicks = 20
        });
        var own = await database.AddUserSegmentAsync(itemId, AnalysisMode.Credits, 30, 40, CancellationToken.None);

        await adapter.ApplyAsync(itemId, [new ProjectedExternalOperation(foreignId, MediaSegmentType.Intro)], CancellationToken.None);

        Assert.Contains((itemId, foreignId), store.DeletedSegments);
        var (replacedItem, replaced) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItem);
        Assert.Equal(own.Id, Assert.Single(replaced).Id);
    }

    [Fact]
    public async Task Apply_DropsTypeMismatchedOperationWithoutDeleting()
    {
        var itemId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var (adapter, store, _) = Create(new MediaSegmentDto
        {
            Id = foreignId,
            ItemId = itemId,
            Type = MediaSegmentType.Outro,
            StartTicks = 10,
            EndTicks = 20
        });

        await adapter.ApplyAsync(itemId, [new ProjectedExternalOperation(foreignId, MediaSegmentType.Intro)], CancellationToken.None);

        // The row changed hands since validation: dropped, not deleted, not retried.
        Assert.Empty(store.DeletedSegments);
    }

    [Fact]
    public async Task Apply_MissingForeignRowIsIdempotentSuccess()
    {
        var itemId = Guid.NewGuid();
        var (adapter, store, _) = Create();

        await adapter.ApplyAsync(itemId, [new ProjectedExternalOperation(Guid.NewGuid(), MediaSegmentType.Intro)], CancellationToken.None);

        Assert.Empty(store.DeletedSegments);
    }

    [Fact]
    public async Task ResolveExternalTarget_ReportsActualOwnerAcrossItems()
    {
        var otherItemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var (adapter, _, _) = Create(new MediaSegmentDto
        {
            Id = segmentId,
            ItemId = otherItemId,
            Type = MediaSegmentType.Intro,
            StartTicks = 10,
            EndTicks = 20
        });

        var target = await adapter.ResolveExternalTargetAsync(Guid.NewGuid(), segmentId, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(otherItemId, target.ItemId);
        Assert.Null(await adapter.ResolveExternalTargetAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    private static (JellyfinSegmentProjectionAdapter Adapter, FakeJellyfinSegmentStore Store, IIntroSkipperDatabase Database) Create(params MediaSegmentDto[] existingSegments)
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
