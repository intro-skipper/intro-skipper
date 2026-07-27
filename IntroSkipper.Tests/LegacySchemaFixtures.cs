// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace IntroSkipper.Tests;

/// <summary>
/// One segment row of a legacy database fixture (seconds-based, pre-v2 shape).
/// </summary>
/// <param name="ItemId">Item id.</param>
/// <param name="Start">Start in seconds.</param>
/// <param name="End">End in seconds.</param>
/// <param name="Type">Numeric analysis mode.</param>
/// <param name="IsUserProvided">Legacy user flag (ignored by shapes without the column).</param>
/// <param name="ConfigHash">Legacy config hash (ignored by shapes without the column).</param>
internal sealed record LegacySegmentRow(Guid ItemId, double Start, double End, int Type, bool IsUserProvided = false, string ConfigHash = "");

/// <summary>
/// One season row of a legacy database fixture.
/// </summary>
/// <param name="SeasonId">Season id.</param>
/// <param name="Type">Numeric analysis mode.</param>
/// <param name="Action">Numeric analyzer action.</param>
/// <param name="EpisodeIdsJson">JSON array of episode ids (may be deliberately malformed).</param>
/// <param name="ConfigHash">Legacy config hash (ignored by shapes without the column).</param>
/// <param name="SettledJson">JSON array of settled ids (ignored by shapes without the column).</param>
internal sealed record LegacySeasonRow(Guid SeasonId, int Type, int Action, string EpisodeIdsJson, string ConfigHash = "", string SettledJson = "[]");

/// <summary>
/// Builds raw SQLite files replicating every historical shape of the legacy
/// <c>introskipper.db</c>, for exercising <c>LegacyDatabaseImporter</c>. Shape
/// detection in the importer is column-driven, so fixtures deliberately skip
/// <c>__EFMigrationsHistory</c> — repair-era databases did not always have one.
/// </summary>
internal static class LegacySchemaFixtures
{
    /// <summary>2024-11 InitialCreate: PK (ItemId, Type), no Id/IsUserProvided/ConfigHash; DbSeasonInfo.</summary>
    /// <param name="path">Database file path.</param>
    /// <param name="segments">Segment rows.</param>
    /// <param name="seasons">Season rows.</param>
    internal static void CreateV0(string path, IReadOnlyList<LegacySegmentRow> segments, IReadOnlyList<LegacySeasonRow> seasons)
        => Create(path, hasId: false, hasUser: false, hasHash: false, useSeasonState: false, segments, seasons);

    /// <summary>2026-03-09 +IsUserProvided (still composite PK); DbSeasonInfo.</summary>
    /// <param name="path">Database file path.</param>
    /// <param name="segments">Segment rows.</param>
    /// <param name="seasons">Season rows.</param>
    internal static void CreateV1(string path, IReadOnlyList<LegacySegmentRow> segments, IReadOnlyList<LegacySeasonRow> seasons)
        => Create(path, hasId: false, hasUser: true, hasHash: false, useSeasonState: false, segments, seasons);

    /// <summary>2026-03-14 identity rebuild: Id INTEGER PK AUTOINCREMENT; DbSeasonInfo.</summary>
    /// <param name="path">Database file path.</param>
    /// <param name="segments">Segment rows.</param>
    /// <param name="seasons">Season rows.</param>
    internal static void CreateV2(string path, IReadOnlyList<LegacySegmentRow> segments, IReadOnlyList<LegacySeasonRow> seasons)
        => Create(path, hasId: true, hasUser: true, hasHash: false, useSeasonState: false, segments, seasons);

    /// <summary>2026-05-19 +ConfigHash on both tables; DbSeasonInfo.</summary>
    /// <param name="path">Database file path.</param>
    /// <param name="segments">Segment rows.</param>
    /// <param name="seasons">Season rows.</param>
    internal static void CreateV4(string path, IReadOnlyList<LegacySegmentRow> segments, IReadOnlyList<LegacySeasonRow> seasons)
        => Create(path, hasId: true, hasUser: true, hasHash: true, useSeasonState: false, segments, seasons);

    /// <summary>2026-06-13 current legacy shape: DbSeasonState with SettledReanalysisEpisodeIds.</summary>
    /// <param name="path">Database file path.</param>
    /// <param name="segments">Segment rows.</param>
    /// <param name="seasons">Season rows.</param>
    internal static void CreateV5(string path, IReadOnlyList<LegacySegmentRow> segments, IReadOnlyList<LegacySeasonRow> seasons)
        => Create(path, hasId: true, hasUser: true, hasHash: true, useSeasonState: true, segments, seasons);

    private static void Create(
        string path,
        bool hasId,
        bool hasUser,
        bool hasHash,
        bool useSeasonState,
        IReadOnlyList<LegacySegmentRow> segments,
        IReadOnlyList<LegacySeasonRow> seasons)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        var segmentColumns = "\"ItemId\" TEXT NOT NULL, \"Type\" INTEGER NOT NULL, \"Start\" REAL NOT NULL DEFAULT 0.0, \"End\" REAL NOT NULL DEFAULT 0.0";
        if (hasUser)
        {
            segmentColumns += ", \"IsUserProvided\" INTEGER NOT NULL DEFAULT 0";
        }

        if (hasHash)
        {
            segmentColumns += ", \"ConfigHash\" TEXT NOT NULL DEFAULT ''";
        }

        var segmentDdl = hasId
            ? $"CREATE TABLE \"DbSegment\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_DbSegment\" PRIMARY KEY AUTOINCREMENT, {segmentColumns})"
            : $"CREATE TABLE \"DbSegment\" ({segmentColumns}, CONSTRAINT \"PK_DbSegment\" PRIMARY KEY (\"ItemId\", \"Type\"))";

        var seasonTable = useSeasonState ? "DbSeasonState" : "DbSeasonInfo";
        var seasonColumns = "\"SeasonId\" TEXT NOT NULL, \"Type\" INTEGER NOT NULL, \"Action\" INTEGER NOT NULL DEFAULT 0, \"EpisodeIds\" TEXT NOT NULL";
        if (hasHash)
        {
            seasonColumns += ", \"ConfigHash\" TEXT NOT NULL DEFAULT ''";
        }

        if (useSeasonState)
        {
            seasonColumns += ", \"SettledReanalysisEpisodeIds\" TEXT NOT NULL DEFAULT '[]'";
        }

        var seasonDdl = $"CREATE TABLE \"{seasonTable}\" ({seasonColumns}, CONSTRAINT \"PK_{seasonTable}\" PRIMARY KEY (\"SeasonId\", \"Type\"))";

        Execute(connection, segmentDdl);
        Execute(connection, seasonDdl);

        foreach (var row in segments)
        {
            var columns = "\"ItemId\", \"Type\", \"Start\", \"End\""
                + (hasUser ? ", \"IsUserProvided\"" : string.Empty)
                + (hasHash ? ", \"ConfigHash\"" : string.Empty);
            var values = "$itemId, $type, $start, $end"
                + (hasUser ? ", $user" : string.Empty)
                + (hasHash ? ", $hash" : string.Empty);

            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO \"DbSegment\" ({columns}) VALUES ({values})";
            command.Parameters.AddWithValue("$itemId", row.ItemId.ToString().ToUpperInvariant());
            command.Parameters.AddWithValue("$type", row.Type);
            command.Parameters.AddWithValue("$start", row.Start);
            command.Parameters.AddWithValue("$end", row.End);
            if (hasUser)
            {
                command.Parameters.AddWithValue("$user", row.IsUserProvided ? 1 : 0);
            }

            if (hasHash)
            {
                command.Parameters.AddWithValue("$hash", row.ConfigHash);
            }

            command.ExecuteNonQuery();
        }

        foreach (var row in seasons)
        {
            var columns = "\"SeasonId\", \"Type\", \"Action\", \"EpisodeIds\""
                + (hasHash ? ", \"ConfigHash\"" : string.Empty)
                + (useSeasonState ? ", \"SettledReanalysisEpisodeIds\"" : string.Empty);
            var values = "$seasonId, $type, $action, $episodeIds"
                + (hasHash ? ", $hash" : string.Empty)
                + (useSeasonState ? ", $settled" : string.Empty);

            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO \"{seasonTable}\" ({columns}) VALUES ({values})";
            command.Parameters.AddWithValue("$seasonId", row.SeasonId.ToString().ToUpperInvariant());
            command.Parameters.AddWithValue("$type", row.Type);
            command.Parameters.AddWithValue("$action", row.Action);
            command.Parameters.AddWithValue("$episodeIds", row.EpisodeIdsJson);
            if (hasHash)
            {
                command.Parameters.AddWithValue("$hash", row.ConfigHash);
            }

            if (useSeasonState)
            {
                command.Parameters.AddWithValue("$settled", row.SettledJson);
            }

            command.ExecuteNonQuery();
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixture DDL assembled from compile-time literals.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// JSON array literal for one guid, matching the legacy EF primitive-collection format.
    /// </summary>
    /// <param name="id">The guid.</param>
    /// <returns>The JSON array.</returns>
    internal static string GuidArrayJson(Guid id)
        => string.Create(CultureInfo.InvariantCulture, $"[\"{id}\"]");
}
