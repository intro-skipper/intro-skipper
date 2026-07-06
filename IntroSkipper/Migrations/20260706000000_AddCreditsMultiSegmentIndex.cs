// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditsMultiSegmentIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            const int creditsType = 1;
            const int commercialType = 4;

            migrationBuilder.DropIndex(
                name: "IX_DbSegment_NonCommercial_Unique",
                table: "DbSegment");

            migrationBuilder.Sql(
                $$"""
                DELETE FROM "DbSegment"
                WHERE "Type" = {{creditsType}}
                AND "Id" NOT IN (
                    SELECT MAX("Id")
                    FROM "DbSegment"
                    WHERE "Type" = {{creditsType}}
                    GROUP BY "ItemId", "Type", "Start", "End"
                );
                DELETE FROM "DbSegment"
                WHERE "Type" != {{commercialType}} AND "Type" != {{creditsType}}
                AND "Id" NOT IN (
                    SELECT MAX("Id")
                    FROM "DbSegment"
                    WHERE "Type" != {{commercialType}} AND "Type" != {{creditsType}}
                    GROUP BY "ItemId", "Type"
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_DbSegment_Credits_Unique",
                table: "DbSegment",
                columns: ["ItemId", "Type", "Start", "End"],
                unique: true,
                filter: $"Type = {creditsType}");

            migrationBuilder.CreateIndex(
                name: "IX_DbSegment_NonCommercial_Unique",
                table: "DbSegment",
                columns: ["ItemId", "Type"],
                unique: true,
                filter: $"Type != {commercialType} AND Type != {creditsType}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            const int commercialType = 4;

            migrationBuilder.DropIndex(
                name: "IX_DbSegment_Credits_Unique",
                table: "DbSegment");

            migrationBuilder.DropIndex(
                name: "IX_DbSegment_NonCommercial_Unique",
                table: "DbSegment");

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
                """);

            migrationBuilder.CreateIndex(
                name: "IX_DbSegment_NonCommercial_Unique",
                table: "DbSegment",
                columns: ["ItemId", "Type"],
                unique: true,
                filter: $"Type != {commercialType}");
        }
    }
}
