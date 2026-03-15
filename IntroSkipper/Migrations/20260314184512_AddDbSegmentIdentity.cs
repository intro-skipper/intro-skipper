using System;
using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddDbSegmentIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var commercialType = (int)AnalysisMode.Commercial;
            migrationBuilder.Sql(
                $"""
                BEGIN TRANSACTION;
                CREATE TABLE "DbSegment_Temp" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_DbSegment_Temp" PRIMARY KEY AUTOINCREMENT,
                    "ItemId" TEXT NOT NULL,
                    "Type" INTEGER NOT NULL,
                    "Start" REAL NOT NULL DEFAULT 0.0,
                    "End" REAL NOT NULL DEFAULT 0.0,
                    "IsUserProvided" INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO "DbSegment_Temp" ("ItemId", "Type", "Start", "End", "IsUserProvided")
                SELECT "ItemId", "Type", "Start", "End", COALESCE("IsUserProvided", 0)
                FROM "DbSegment";
                DROP TABLE "DbSegment";
                ALTER TABLE "DbSegment_Temp" RENAME TO "DbSegment";
                CREATE INDEX "IX_DbSegment_ItemId" ON "DbSegment" ("ItemId");
                CREATE UNIQUE INDEX "IX_DbSegment_Commercial_Unique" ON "DbSegment" ("ItemId", "Type", "Start", "End")
                    WHERE "Type" = {commercialType};
                COMMIT;
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                BEGIN TRANSACTION;
                CREATE TABLE "DbSegment_Temp" (
                    "ItemId" TEXT NOT NULL,
                    "Type" INTEGER NOT NULL,
                    "Start" REAL NOT NULL DEFAULT 0.0,
                    "End" REAL NOT NULL DEFAULT 0.0,
                    "IsUserProvided" INTEGER NOT NULL DEFAULT 0,
                    CONSTRAINT "PK_DbSegment_Temp" PRIMARY KEY ("ItemId", "Type")
                );
                INSERT INTO "DbSegment_Temp" ("ItemId", "Type", "Start", "End", "IsUserProvided")
                SELECT "ItemId", "Type", "Start", "End", COALESCE("IsUserProvided", 0)
                FROM "DbSegment"
                WHERE "Id" IN (
                    SELECT MIN("Id")
                    FROM "DbSegment"
                    GROUP BY "ItemId", "Type"
                );
                DROP TABLE "DbSegment";
                ALTER TABLE "DbSegment_Temp" RENAME TO "DbSegment";
                CREATE INDEX "IX_DbSegment_ItemId" ON "DbSegment" ("ItemId");
                COMMIT;
                """,
                suppressTransaction: true);
        }
    }
}
