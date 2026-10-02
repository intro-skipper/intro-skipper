// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;
using Xunit;

/// <summary>
/// Tests for <see cref="MediaSegmentMirror"/>: uniform per-item convergence, validated
/// foreign-row deletes, and the disabled no-op. Every write lands in the store fake, so
/// the assertions see exactly what Jellyfin would.
/// </summary>
public sealed class TestMediaSegmentMirror
{
    [Fact]
    public async Task Apply_UsesUniformReplace_ForEveryMode()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(1200, 1260))], SegmentSource.Chromaprint);
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Commercial,
            [new Segment(itemId, new TimeRange(300, 330)), new Segment(itemId, new TimeRange(600, 630))],
            SegmentSource.BlackFrame);
        await database.SeedUserSegmentAsync(
            itemId, AnalysisMode.Recap, TickConversions.FromSeconds(20), TickConversions.FromSeconds(40));
        var rows = await database.GetSegmentsAsync(itemId);
        Assert.Equal(5, rows.Count);

        var store = new FakeJellyfinSegmentStore();
        var mirror = DatabaseTestHelpers.CreateMirror(store, database);

        Assert.True(await mirror.ApplyAsync(itemId, [], CancellationToken.None));

        // One uniform replace carries every active segment of every mode (no per-type
        // routing, no commercial special case), and each DTO reuses its plugin row's id.
        Assert.Equal(1, store.WriteCallCount);
        var (replacedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        Assert.Equal(rows.Count, pushed.Count);
        foreach (var row in rows)
        {
            var dto = Assert.Single(pushed, segment => segment.Id == row.Id);
            Assert.Equal(itemId, dto.ItemId);
            Assert.Equal(row.StartTicks, dto.StartTicks);
            Assert.Equal(row.EndTicks, dto.EndTicks);
        }

        Assert.Equal(2, pushed.Count(segment => segment.Type == MediaSegmentType.Commercial));

        // Jellyfin now matches, so a second apply reads but does not write.
        Assert.True(await mirror.ApplyAsync(itemId, [], CancellationToken.None));
        Assert.Equal(1, store.WriteCallCount);
    }

    [Fact]
    public async Task Apply_DeletesValidatedForeignRowAndSyncsImage()
    {
        var itemId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var (mirror, store, database) = Create(SegmentChangeHarness.MirroredDto(itemId, foreignId, MediaSegmentType.Intro, 10, 20));
        var own = await database.SeedUserSegmentAsync(itemId, AnalysisMode.Credits, 30, 40);

        Assert.True(await mirror.ApplyAsync(itemId, [Delete(foreignId)], CancellationToken.None));

        Assert.Equal([(itemId, foreignId)], store.DeletedSegments);
        Assert.Empty(store.ForeignSegments);
        var (replacedItem, replaced) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItem);
        Assert.Equal(own.Id, Assert.Single(replaced).Id);
    }

    // The operation was validated as Intro 10..20; the row was rewritten under its
    // stable id since then (type or boundaries). The predicate travels inside the
    // delete, so the row stays in place, and the operation is dropped and still counts
    // as done.
    [Theory]
    [InlineData(MediaSegmentType.Outro, 10, 20)]
    [InlineData(MediaSegmentType.Intro, 100, 200)]
    public async Task Apply_DropsRewrittenRowOperationWithoutDeleting(MediaSegmentType currentType, long currentStart, long currentEnd)
    {
        var itemId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var (mirror, store, _) = Create(SegmentChangeHarness.MirroredDto(itemId, foreignId, currentType, currentStart, currentEnd));

        Assert.True(await mirror.ApplyAsync(itemId, [Delete(foreignId)], CancellationToken.None));

        Assert.Empty(store.DeletedSegments);
        Assert.True(store.ForeignSegments.ContainsKey(foreignId));
    }

    [Fact]
    public async Task Apply_MissingForeignRowIsIdempotentSuccess()
    {
        var itemId = Guid.NewGuid();
        var (mirror, store, _) = Create();

        Assert.True(await mirror.ApplyAsync(itemId, [Delete(Guid.NewGuid())], CancellationToken.None));

        Assert.Empty(store.DeletedSegments);
    }

    [Fact]
    public async Task Apply_DoesNotTouchJellyfin_WhenUpdateMediaSegmentsDisabled()
    {
        var itemId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore();
        store.ForeignSegments[foreignId] = SegmentChangeHarness.MirroredDto(itemId, foreignId, MediaSegmentType.Intro, 10, 20);
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(30, 40))], SegmentSource.Chapter);
        var mirror = DatabaseTestHelpers.CreateMirror(store, database, new FakeMirrorPolicy { Enabled = false });

        // The mirror flag lives in the mirror, not at call sites: an apply with both a
        // journaled delete and a sync to do reports the disabled outcome and leaves the
        // store untouched.
        Assert.False(await mirror.ApplyAsync(itemId, [Delete(foreignId)], CancellationToken.None));

        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(store.DeletedSegments);
        Assert.True(store.ForeignSegments.ContainsKey(foreignId));
    }

    /// <summary>A journaled delete validated as Intro 10..20.</summary>
    private static DbProjectionExternalOperation Delete(Guid externalSegmentId)
        => new() { ExternalSegmentId = externalSegmentId, ExpectedType = MediaSegmentType.Intro, StartTicks = 10, EndTicks = 20 };

    private static (MediaSegmentMirror Mirror, FakeJellyfinSegmentStore Store, IntroSkipperDatabase Database) Create(params MediaSegmentDto[] foreignSegments)
    {
        var store = new FakeJellyfinSegmentStore();
        foreach (var segment in foreignSegments)
        {
            store.ForeignSegments[segment.Id] = segment;
        }

        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        return (DatabaseTestHelpers.CreateMirror(store, database), store, database);
    }
}
