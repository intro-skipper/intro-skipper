// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddNonCommercialUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var commercialType = (int)AnalysisMode.Commercial;

            // Remove duplicate non-commercial segments before enforcing uniqueness.
            // For each (ItemId, Type) pair, keep the most recently inserted row (MAX Id).
            // User-provided segments are typically inserted last (after analysis), so
            // MAX(Id) naturally preserves them when duplicates exist.
            migrationBuilder.Sql(
                $$"""
                DELETE FROM "DbSegment"
                WHERE "Type" != {{commercialType}}
                AND "Id" NOT IN (
                    SELECT MAX("Id")
                    FROM "DbSegment"
                    WHERE "Type" != {{commercialType}}
                    GROUP BY "ItemId", "Type"
                );
                CREATE UNIQUE INDEX "IX_DbSegment_NonCommercial_Unique" ON "DbSegment" ("ItemId", "Type")
                    WHERE "Type" != {{commercialType}};
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DbSegment_NonCommercial_Unique",
                table: "DbSegment");
        }
    }
}
