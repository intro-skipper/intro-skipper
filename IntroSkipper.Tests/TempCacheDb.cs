// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using IntroSkipper.Db;

/// <summary>
/// A temp-file detection cache database whose files are deleted on dispose. The
/// facade creates the schema on first use.
/// </summary>
internal sealed class TempCacheDb : IDisposable
{
    private DetectionCacheDatabase? _database;

    public string Path { get; } = DatabaseTestHelpers.CreateTempCacheDbPath();

    public DetectionCacheDatabase Database => _database ??= CreateDatabase();

    public DetectionCacheDatabase CreateDatabase() => DatabaseTestHelpers.CreateCacheDatabase(Path);

    public DetectionCacheDbContext Context() => DatabaseTestHelpers.CreateCacheContext(Path);

    public void Dispose() => DatabaseTestHelpers.DeleteSqliteFiles(Path);
}
