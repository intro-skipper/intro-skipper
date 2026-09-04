// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using IntroSkipper.Db;

/// <summary>
/// A temp-file segment database (<c>introskipper-v2.db</c>) whose files are deleted
/// on dispose. <see cref="Database"/> is one facade over the file; tests that need
/// independent initialization gates over the same file call
/// <see cref="CreateDatabase"/> again.
/// </summary>
internal sealed class TempSegmentDb : IDisposable
{
    private IntroSkipperDatabase? _database;

    public string Path { get; } = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".db");

    public IntroSkipperDatabase Database => _database ??= CreateDatabase();

    public IntroSkipperDatabase CreateDatabase() => DatabaseTestHelpers.CreateSegmentDatabase(Path);

    public IntroSkipperDbContext Context() => DatabaseTestHelpers.CreateSegmentContext(Path);

    public void Dispose() => DatabaseTestHelpers.DeleteSqliteFiles(Path);
}
