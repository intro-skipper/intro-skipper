// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.SegmentChanges;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The production segment-change composition over a temp segment database and a
/// <see cref="FakeJellyfinSegmentStore"/>: facade, journal, mirror and coordinator.
/// Controller tests build their controller over <see cref="Change"/>; journaling
/// tests inspect the queue through <see cref="QueuedItemIdsAsync"/>.
/// </summary>
internal sealed class SegmentChangeHarness : IDisposable
{
    private readonly TempSegmentDb _db = new();
    private FakeJellyfinSegmentStore _store;
    private SegmentChange? _change;

    public SegmentChangeHarness(FakeJellyfinSegmentStore? store = null)
    {
        _store = store ?? new FakeJellyfinSegmentStore();
    }

    public IntroSkipperDatabase Database => _db.Database;

    public string DbPath => _db.Path;

    /// <summary>
    /// Gets or sets the Jellyfin store the coordinator projects into. Replace it
    /// before the first <see cref="Change"/> access when the store must be seeded
    /// from rows the database holds.
    /// </summary>
    public FakeJellyfinSegmentStore Store
    {
        get => _store;
        set => _store = _change is null ? value : throw new InvalidOperationException("The change coordinator is already composed over the previous store.");
    }

    public SegmentChange Change => _change ??= DatabaseTestHelpers.CreateSegmentChange(Store, Database);

    public IntroSkipperDbContext Context() => _db.Context();

    /// <summary>
    /// A row as Jellyfin's mirror already holds it. Seeded into a store so a sync
    /// whose intended push differs (a disable withdrawing it, a plugin-side change) is
    /// a real write rather than a skipped no-op; the defaults match no plugin row.
    /// </summary>
    public static MediaSegmentDto MirroredDto(Guid itemId, Guid? id = null, MediaSegmentType type = MediaSegmentType.Intro, long? startTicks = null, long? endTicks = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            ItemId = itemId,
            Type = type,
            StartTicks = startTicks ?? TickConversions.FromSeconds(10),
            EndTicks = endTicks ?? TickConversions.FromSeconds(20),
        };

    /// <summary>A cache database path whose directory does not exist, so every cache operation fails.</summary>
    public static string MissingCachePath()
        => Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "missing-cache", Guid.NewGuid().ToString("N"), "cache.db");

    public async Task<Guid[]> QueuedItemIdsAsync()
    {
        await using var db = Context();
        return await db.ProjectionQueue.Select(q => q.ItemId).OrderBy(id => id).ToArrayAsync();
    }

    public async Task ClearQueueAsync()
    {
        await using var db = Context();
        await db.ProjectionQueue.ExecuteDeleteAsync();
    }

    public void Dispose() => _db.Dispose();
}
