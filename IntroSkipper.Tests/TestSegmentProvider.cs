// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Providers;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Jellyfin calls <see cref="SegmentProvider.CleanupExtractedData"/> when an item's media
/// file changed, then deletes the item's rows from its MediaSegments table. The item still
/// exists, so the cleanup keeps what the user made, as the plugin's own replaced-file reset
/// does.
/// </summary>
public sealed class TestSegmentProvider : IDisposable
{
    private readonly SegmentChangeHarness _h = new();
    private readonly TempCacheDb _cache = new();

    public void Dispose()
    {
        _h.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task CleanupExtractedData_KeepsUserSegmentsAndTombstones_AndProjectsTheUserSegmentsBack()
    {
        var itemId = Guid.NewGuid();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 40))], SegmentSource.Chromaprint);
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(1200, 1260))], SegmentSource.Chapter);
        var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId), s => s.Type == AnalysisMode.Credits);
        await database.DeleteSegmentAsync(itemId, tombstone.Id);
        var user = await database.SeedUserSegmentAsync(itemId, AnalysisMode.Recap, DatabaseTestHelpers.Ticks(0), DatabaseTestHelpers.Ticks(8));
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [itemId], "hash");

        // Jellyfin holds both active rows, as the mirror pushed them.
        _h.Store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [.. (await database.GetSegmentsAsync(itemId)).Select(row => SegmentChangeHarness.MirroredDto(itemId, row.Id, AnalysisHelpers.ModeToSegmentType[row.Type], row.StartTicks, row.EndTicks))],
        };
        await _h.ClearQueueAsync();

        await CreateProvider().CleanupExtractedData(itemId, CancellationToken.None);

        // The erase journals the item, so the worker will push the kept user segment back.
        Assert.Equal([itemId], await _h.QueuedItemIdsAsync());

        var rows = await database.GetSegmentsAsync(itemId, includeSuppressed: true);
        Assert.Equal(
            new[] { (user.Id, SegmentState.Active), (tombstone.Id, SegmentState.Suppressed) }.Order(),
            rows.Select(row => (row.Id, row.State)).Order());
        await using (var db = _h.Context())
        {
            Assert.False(await db.AnalyzedItems.AnyAsync(a => a.ItemId == itemId));
        }

        // Jellyfin empties the item's mirror once the provider returns. The worker then
        // applies the item's journaled marker and pushes the user segment back.
        await _h.Store.ReplaceSegmentsAsync(itemId, [], CancellationToken.None);
        await _h.Change.ProjectItemsAsync([itemId]);

        Assert.Equal(user.Id, Assert.Single(await _h.Store.GetOwnSegmentsAsync(itemId, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task CleanupExtractedData_DeletesTheItemsDetectionCache()
    {
        var itemId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        _cache.Database.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 600, EntrypointTestHelpers.EmptyJsonArray, "hash");
        _cache.Database.Upsert(otherId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 600, EntrypointTestHelpers.EmptyJsonArray, "hash");

        await CreateProvider().CleanupExtractedData(itemId, CancellationToken.None);

        await using var db = _cache.Context();
        Assert.Equal([otherId], await db.DetectionCache.Select(e => e.ItemId).ToListAsync());
    }

    private SegmentProvider CreateProvider() => new(_h.Database, _cache.Database, new SegmentDtoFactory(_h.Database));
}
