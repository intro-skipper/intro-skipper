// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Helpers for constructing the database facades in tests. The plugin-bound variants
/// resolve the database path from <see cref="Plugin.Instance"/> lazily on every
/// operation, matching the way tests swap plugin instances and paths via
/// <see cref="EntrypointTestHelpers.PluginInstanceScope"/>.
/// </summary>
internal static class DatabaseTestHelpers
{
    internal static IntroSkipperDatabase CreateSegmentDatabase(string dbPath)
        => new(new IntroSkipperDbContextPathFactory(() => dbPath), NullLogger.Instance);

    /// <summary>
    /// Creates a segment database facade over a fresh temp-file path, for consumers
    /// (e.g. analyzers) whose tests never reach the database. The file is only created
    /// if an operation is actually performed.
    /// </summary>
    /// <returns>The segment database facade.</returns>
    internal static IntroSkipperDatabase CreateTempSegmentDatabase()
        => CreateSegmentDatabase(Path.Combine(Path.GetTempPath(), "IntroSkipper.Tests", Guid.NewGuid().ToString("N") + ".db"));

    internal static DetectionCacheDatabase CreateCacheDatabase(string dbPath)
        => new(new DetectionCacheDbContextPathFactory(() => dbPath), NullLogger.Instance);

    internal static IntroSkipperDatabase CreatePluginBoundSegmentDatabase()
        => new(new IntroSkipperDbContextPathFactory(() => RequirePlugin().DbPath), NullLogger.Instance);

    internal static DetectionCacheDatabase CreatePluginBoundCacheDatabase()
        => new(new DetectionCacheDbContextPathFactory(() => RequirePlugin().CacheDbPath), NullLogger.Instance);

    internal static DetectionCacheService CreatePluginBoundCacheService()
        => new(NullLogger<DetectionCacheService>.Instance, CreatePluginBoundCacheDatabase());

    private static Plugin RequirePlugin()
        => Plugin.Instance ?? throw new InvalidOperationException("Plugin.Instance is not set.");
}
