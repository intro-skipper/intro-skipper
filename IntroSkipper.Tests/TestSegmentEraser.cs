// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.SegmentChanges;
using Xunit;

/// <summary>
/// Pins which detection cache rows an erase removes: only when the caller asks, and for
/// a mode erase only Introduction and Credits rows. Segment deletion and mirror
/// convergence are covered through the endpoints that call the eraser.
/// </summary>
public sealed class TestSegmentEraser : IDisposable
{
    private readonly SegmentChangeHarness _h = new();
    private readonly TempCacheDb _cache = new();

    public void Dispose()
    {
        _h.Dispose();
        _cache.Dispose();
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction, true, false)]
    [InlineData(AnalysisMode.Credits, true, false)]
    [InlineData(AnalysisMode.Introduction, false, true)]
    [InlineData(AnalysisMode.Recap, true, true)]
    public async Task EraseMode_DeletesCacheRows_OnlyWhenAskedAndForIntroductionOrCredits(AnalysisMode mode, bool eraseCache, bool cacheKept)
    {
        var itemId = Guid.NewGuid();
        SeedCacheRow(itemId, mode);

        await CreateEraser().EraseModeAsync(mode, eraseCache, CancellationToken.None);

        Assert.Equal(cacheKept, HasCacheRow(itemId, mode));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EraseItems_DeletesCacheRows_OnlyWhenAsked(bool eraseCache, bool cacheKept)
    {
        var itemId = Guid.NewGuid();
        SeedCacheRow(itemId, AnalysisMode.Introduction);

        var (_, removedCacheEntries) = await CreateEraser().EraseItemsAsync([itemId], eraseCache, CancellationToken.None);

        Assert.Equal(cacheKept ? 0 : 1, removedCacheEntries);
        Assert.Equal(cacheKept, HasCacheRow(itemId, AnalysisMode.Introduction));
    }

    private SegmentEraser CreateEraser() => new(_h.Database, _cache.Database, _h.Change);

    private void SeedCacheRow(Guid itemId, AnalysisMode mode)
        => _cache.Database.Upsert(itemId, mode, CacheEntryType.Chromaprint, 0, 100, EntrypointTestHelpers.EmptyJsonArray, "hash");

    private bool HasCacheRow(Guid itemId, AnalysisMode mode)
        => _cache.Database.FindEntry(itemId, mode, CacheEntryType.Chromaprint, 0, 100) is not null;
}
