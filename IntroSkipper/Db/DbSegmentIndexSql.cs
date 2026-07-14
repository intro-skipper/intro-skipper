// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Current DbSegment index and duplicate-cleanup SQL shared by migrations and legacy schema repair.
/// </summary>
internal static class DbSegmentIndexSql
{
    /// <summary>
    /// Numeric database value for credits segments.
    /// </summary>
    internal const int CreditsType = (int)AnalysisMode.Credits;

    /// <summary>
    /// Numeric database value for commercial segments.
    /// </summary>
    internal const int CommercialType = (int)AnalysisMode.Commercial;

    /// <summary>
    /// Item lookup index name.
    /// </summary>
    internal const string ItemIndexName = "IX_DbSegment_ItemId";

    /// <summary>
    /// Commercial unique index name.
    /// </summary>
    internal const string CommercialUniqueIndexName = "IX_DbSegment_Commercial_Unique";

    /// <summary>
    /// Credits unique index name.
    /// </summary>
    internal const string CreditsUniqueIndexName = "IX_DbSegment_Credits_Unique";

    /// <summary>
    /// Single-entry segment unique index name.
    /// </summary>
    internal const string NonCommercialUniqueIndexName = "IX_DbSegment_NonCommercial_Unique";

    /// <summary>
    /// Gets the SQLite filter for commercial segments.
    /// </summary>
    internal static string CommercialFilter => $"Type = {CommercialType}";

    /// <summary>
    /// Gets the SQLite filter for credits segments.
    /// </summary>
    internal static string CreditsFilter => $"Type = {CreditsType}";

    /// <summary>
    /// Gets the SQLite filter for segment types that allow only one row per item.
    /// </summary>
    internal static string SingleSegmentFilter => $"Type != {CommercialType} AND Type != {CreditsType}";

    /// <summary>
    /// Gets the SQL that creates the item lookup index.
    /// </summary>
    internal static string CreateItemIndexSql =>
        $$"""
        CREATE INDEX IF NOT EXISTS "{{ItemIndexName}}" ON "DbSegment" ("ItemId")
        """;

    /// <summary>
    /// Gets the SQL that removes duplicate commercial rows before creating the commercial unique index.
    /// </summary>
    internal static string DeleteDuplicateCommercialSegmentsSql =>
        $$"""
        DELETE FROM "DbSegment"
        WHERE "Type" = {{CommercialType}}
        AND "Id" NOT IN (
            SELECT MAX("Id")
            FROM "DbSegment"
            WHERE "Type" = {{CommercialType}}
            GROUP BY "ItemId", "Type", "Start", "End"
        )
        """;

    /// <summary>
    /// Gets the SQL that creates the commercial unique index.
    /// </summary>
    internal static string CreateCommercialUniqueIndexSql =>
        $$"""
        CREATE UNIQUE INDEX IF NOT EXISTS "{{CommercialUniqueIndexName}}" ON "DbSegment" ("ItemId", "Type", "Start", "End")
            WHERE "Type" = {{CommercialType}}
        """;

    /// <summary>
    /// Gets the SQL that removes duplicate credit rows before creating the credits unique index.
    /// </summary>
    internal static string DeleteDuplicateCreditSegmentsSql =>
        $$"""
        DELETE FROM "DbSegment"
        WHERE "Type" = {{CreditsType}}
        AND "Id" NOT IN (
            SELECT MAX("Id")
            FROM "DbSegment"
            WHERE "Type" = {{CreditsType}}
            GROUP BY "ItemId", "Type", "Start", "End"
        )
        """;

    /// <summary>
    /// Gets the SQL that creates the credits unique index.
    /// </summary>
    internal static string CreateCreditsUniqueIndexSql =>
        $$"""
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_DbSegment_Credits_Unique" ON "DbSegment" ("ItemId", "Type", "Start", "End")
            WHERE "Type" = {{CreditsType}}
        """;

    /// <summary>
    /// Gets the SQL that drops the legacy single-row non-commercial unique index.
    /// </summary>
    internal static string DropNonCommercialUniqueIndexSql =>
        """
        DROP INDEX IF EXISTS "IX_DbSegment_NonCommercial_Unique"
        """;

    /// <summary>
    /// Gets the SQL that removes duplicate single-entry segment rows before creating the single-entry unique index.
    /// </summary>
    internal static string DeleteDuplicateSingleSegmentsSql =>
        $$"""
        DELETE FROM "DbSegment"
        WHERE "Type" != {{CommercialType}} AND "Type" != {{CreditsType}}
        AND "Id" NOT IN (
            SELECT MAX("Id")
            FROM "DbSegment"
            WHERE "Type" != {{CommercialType}} AND "Type" != {{CreditsType}}
            GROUP BY "ItemId", "Type"
        )
        """;

    /// <summary>
    /// Gets the SQL that creates the unique index for segment types that allow only one row per item.
    /// </summary>
    internal static string CreateNonCommercialUniqueIndexSql =>
        $$"""
        CREATE UNIQUE INDEX IF NOT EXISTS "{{NonCommercialUniqueIndexName}}" ON "DbSegment" ("ItemId", "Type")
            WHERE "Type" != {{CommercialType}} AND "Type" != {{CreditsType}}
        """;
}
