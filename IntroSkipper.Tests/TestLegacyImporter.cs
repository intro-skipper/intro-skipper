// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// One-time legacy import: every historical shape of <c>introskipper.db</c> must be
/// carried into <c>introskipper-v2.db</c> without the legacy file ever being modified.
/// The importer runs inside the facade's initialization, keyed off the legacy file
/// sitting next to the v2 database file.
/// </summary>
public sealed class TestLegacyImporter
{
    public enum LegacyShape
    {
        V0InitialCreate,
        V1IsUserProvided,
        V2Identity,
        V4ConfigHashes,
        V5SeasonState
    }

    [Theory]
    [InlineData(LegacyShape.V0InitialCreate)]
    [InlineData(LegacyShape.V1IsUserProvided)]
    [InlineData(LegacyShape.V2Identity)]
    [InlineData(LegacyShape.V4ConfigHashes)]
    [InlineData(LegacyShape.V5SeasonState)]
    public async Task Import_AllHistoricalShapes_ProducesExpectedRows(LegacyShape shape)
    {
        using var scope = new FixtureScope();
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        var hasUser = shape != LegacyShape.V0InitialCreate;
        var hasHash = shape is LegacyShape.V4ConfigHashes or LegacyShape.V5SeasonState;

        LegacySegmentRow[] segments =
        [
            new(itemA, 10, 60, (int)AnalysisMode.Introduction, IsUserProvided: true, ConfigHash: "legacy-hash"),
            new(itemA, 1200, 1260, (int)AnalysisMode.Credits),
            new(itemB, 100, 110, (int)AnalysisMode.Commercial),
            new(itemB, 10, 0, (int)AnalysisMode.Introduction),   // invalid: End <= 0 → dropped
            new(itemB, 50, 40, (int)AnalysisMode.Credits),       // invalid: End <= Start → dropped
            new(itemB, 5, 6, 99)                                 // invalid: unknown mode → dropped
        ];
        LegacySeasonRow[] seasons =
        [
            new(seasonId, (int)AnalysisMode.Introduction, (int)AnalyzerAction.Chromaprint, LegacySchemaFixtures.GuidArrayJson(episodeId), "season-hash", LegacySchemaFixtures.GuidArrayJson(episodeId))
        ];

        CreateFixture(shape, scope.LegacyPath, segments, seasons);

        var database = DatabaseTestHelpers.CreateSegmentDatabase(scope.V2Path);
        await database.InitializeAsync();

        await using var db = new IntroSkipperDbContext(scope.V2Path);
        var imported = await db.Segments.AsNoTracking().ToListAsync();
        Assert.Equal(3, imported.Count);
        Assert.All(imported, s => Assert.Equal(SegmentState.Active, s.State));
        Assert.All(imported, s => Assert.NotEqual(Guid.Empty, s.Id));
        Assert.Equal(imported.Count, imported.Select(s => s.Id).Distinct().Count());

        var intro = Assert.Single(imported, s => s.Type == AnalysisMode.Introduction);
        Assert.Equal(itemA, intro.ItemId);
        Assert.Equal(TickConversions.FromSeconds(10), intro.StartTicks);
        Assert.Equal(TickConversions.FromSeconds(60), intro.EndTicks);
        Assert.Equal(hasUser ? SegmentSource.User : SegmentSource.Unknown, intro.Source);
        Assert.Equal(hasHash ? "legacy-hash" : string.Empty, intro.ConfigHash);

        var credits = Assert.Single(imported, s => s.Type == AnalysisMode.Credits);
        Assert.Equal(SegmentSource.Unknown, credits.Source);
        Assert.Single(imported, s => s.Type == AnalysisMode.Commercial);

        var state = Assert.Single(await db.SeasonStates.AsNoTracking().ToListAsync());
        Assert.Equal(seasonId, state.SeasonId);
        Assert.Equal(AnalysisMode.Introduction, state.Type);
        Assert.Equal(AnalyzerAction.Chromaprint, state.Action);
        Assert.Equal(
            shape == LegacyShape.V5SeasonState ? [episodeId] : Array.Empty<Guid>(),
            state.SettledReanalysisEpisodeIds);

        // The legacy analyzed-episode list becomes per-item analysis records under the
        // season's hash.
        var analyzed = Assert.Single(await db.AnalyzedItems.AsNoTracking().ToListAsync());
        Assert.Equal(episodeId, analyzed.ItemId);
        Assert.Equal(AnalysisMode.Introduction, analyzed.Type);
        Assert.Equal(hasHash ? "season-hash" : string.Empty, analyzed.ConfigHash);

        var marker = Assert.Single(await db.ImportHistory.AsNoTracking().ToListAsync());
        Assert.True(marker.SourceFileFound);
        Assert.Equal(3, marker.SegmentsImported);
        Assert.Equal(3, marker.SegmentsSkipped);
        Assert.Equal(1, marker.SeasonStatesImported);
    }

    [Fact]
    public async Task Import_CollapsesExactDuplicates_UserRowWins()
    {
        using var scope = new FixtureScope();
        var itemId = Guid.NewGuid();

        // Repair-era databases could lack the unique indexes entirely, so true
        // duplicates exist. The user-flagged copy must survive the collapse.
        LegacySchemaFixtures.CreateV4(
            scope.LegacyPath,
            [
                new(itemId, 10, 20, (int)AnalysisMode.Commercial, IsUserProvided: false),
                new(itemId, 10, 20, (int)AnalysisMode.Commercial, IsUserProvided: true),
                new(itemId, 10, 20, (int)AnalysisMode.Commercial, IsUserProvided: false)
            ],
            []);

        await DatabaseTestHelpers.CreateSegmentDatabase(scope.V2Path).InitializeAsync();

        await using var db = new IntroSkipperDbContext(scope.V2Path);
        var row = Assert.Single(await db.Segments.AsNoTracking().ToListAsync());
        Assert.Equal(SegmentSource.User, row.Source);
    }

    [Fact]
    public async Task Import_RunsOnce_SecondInitializationSkipsImport()
    {
        using var scope = new FixtureScope();
        var itemId = Guid.NewGuid();
        LegacySchemaFixtures.CreateV5(
            scope.LegacyPath,
            [new(itemId, 10, 60, (int)AnalysisMode.Introduction)],
            []);

        await DatabaseTestHelpers.CreateSegmentDatabase(scope.V2Path).InitializeAsync();

        // A second, independent facade over the same file: the marker answers the
        // import question, so nothing is imported again.
        await DatabaseTestHelpers.CreateSegmentDatabase(scope.V2Path).InitializeAsync();

        await using var db = new IntroSkipperDbContext(scope.V2Path);
        Assert.Equal(1, await db.Segments.CountAsync());
        Assert.Equal(1, await db.ImportHistory.CountAsync());
    }

    [Fact]
    public async Task Import_MalformedValues_SkipsBadRowsAndDegradesBadJsonWithoutAbortingImport()
    {
        using var scope = new FixtureScope();
        var itemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var malformedJsonSeasonId = Guid.NewGuid();
        LegacySchemaFixtures.CreateV5(
            scope.LegacyPath,
            [new(itemId, 10, 60, (int)AnalysisMode.Introduction, IsUserProvided: true)],
            [
                new(seasonId, (int)AnalysisMode.Introduction, (int)AnalyzerAction.Chromaprint, "[]"),

                // Malformed EpisodeIds JSON degrades to an empty list; the row and its action survive.
                new(malformedJsonSeasonId, (int)AnalysisMode.Credits, (int)AnalyzerAction.Chapter, "not-json")
            ]);

        // SQLite never enforced column affinity, so repair-era files can carry text
        // where numbers belong. Plant such rows directly.
        var builder = new SqliteConnectionStringBuilder { DataSource = scope.LegacyPath, Pooling = false };
        var legacy = new SqliteConnection(builder.ToString());
        await using (legacy.ConfigureAwait(false))
        {
            await legacy.OpenAsync();
            using var command = legacy.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "DbSegment" ("ItemId", "Start", "End", "Type", "IsUserProvided", "ConfigHash")
                VALUES ($item, 'garbage', 90, 0, 'not-a-flag', '');
                INSERT INTO "DbSeasonState" ("SeasonId", "Type", "Action", "EpisodeIds", "ConfigHash", "SettledReanalysisEpisodeIds")
                VALUES ($season, 'garbage', 'garbage', '[]', '', '[]');
                """;
            command.Parameters.AddWithValue("$item", itemId.ToString());
            command.Parameters.AddWithValue("$season", Guid.NewGuid().ToString());
            await command.ExecuteNonQueryAsync();
        }

        await DatabaseTestHelpers.CreateSegmentDatabase(scope.V2Path).InitializeAsync();

        await using var db = new IntroSkipperDbContext(scope.V2Path);
        var row = Assert.Single(await db.Segments.AsNoTracking().ToListAsync());
        Assert.Equal(itemId, row.ItemId);
        Assert.Equal(SegmentSource.User, row.Source);

        var states = await db.SeasonStates.AsNoTracking().ToListAsync();
        Assert.Equal(2, states.Count);
        Assert.Contains(states, s => s.SeasonId == seasonId);
        var degraded = Assert.Single(states, s => s.SeasonId == malformedJsonSeasonId);
        Assert.Equal(AnalyzerAction.Chapter, degraded.Action);
        Assert.Empty(await db.AnalyzedItems.AsNoTracking().ToListAsync());

        var marker = Assert.Single(await db.ImportHistory.AsNoTracking().ToListAsync());
        Assert.Equal(1, marker.SegmentsImported);
        Assert.Equal(1, marker.SegmentsSkipped);
        Assert.Equal(2, marker.SeasonStatesImported);
    }

    private static void CreateFixture(LegacyShape shape, string path, LegacySegmentRow[] segments, LegacySeasonRow[] seasons)
    {
        switch (shape)
        {
            case LegacyShape.V0InitialCreate:
                // The composite (ItemId, Type) PK cannot hold two rows of the same
                // mode for one item — drop rows that would collide, mirroring reality.
                var deduped = segments.GroupBy(s => (s.ItemId, s.Type)).Select(g => g.First()).ToArray();
                LegacySchemaFixtures.CreateV0(path, deduped, seasons);
                break;
            case LegacyShape.V1IsUserProvided:
                LegacySchemaFixtures.CreateV1(path, segments.GroupBy(s => (s.ItemId, s.Type)).Select(g => g.First()).ToArray(), seasons);
                break;
            case LegacyShape.V2Identity:
                LegacySchemaFixtures.CreateV2(path, segments, seasons);
                break;
            case LegacyShape.V4ConfigHashes:
                LegacySchemaFixtures.CreateV4(path, segments, seasons);
                break;
            case LegacyShape.V5SeasonState:
                LegacySchemaFixtures.CreateV5(path, segments, seasons);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    /// <summary>
    /// Isolated temp directory containing a legacy <c>introskipper.db</c> next to the
    /// v2 database path, mirroring the production plugin data directory.
    /// </summary>
    private sealed class FixtureScope : IDisposable
    {
        private readonly string _directory;

        public FixtureScope()
        {
            _directory = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "legacy-import", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            LegacyPath = Path.Join(_directory, "introskipper.db");
            V2Path = Path.Join(_directory, "introskipper-v2.db");
        }

        public string LegacyPath { get; }

        public string V2Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
