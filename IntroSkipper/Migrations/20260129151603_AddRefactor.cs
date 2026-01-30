using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddRefactor : Migration
    {
        private static readonly string[] _outboxIndexColumns = ["ItemId", "Type", "SegmentIndex"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop EpisodeIds column from DbSeasonInfo if it exists
            migrationBuilder.Sql(
                @"CREATE TABLE IF NOT EXISTS ""DbSeasonInfo_new"" (
                    ""SeasonId"" TEXT NOT NULL,
                    ""Type"" INTEGER NOT NULL,
                    ""Action"" INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (""SeasonId"", ""Type"")
                );

                INSERT OR IGNORE INTO ""DbSeasonInfo_new"" (""SeasonId"", ""Type"", ""Action"")
                SELECT ""SeasonId"", ""Type"", ""Action"" FROM ""DbSeasonInfo"";

                DROP TABLE IF EXISTS ""DbSeasonInfo"";

                ALTER TABLE ""DbSeasonInfo_new"" RENAME TO ""DbSeasonInfo"";

                CREATE INDEX IF NOT EXISTS ""IX_DbSeasonInfo_SeasonId"" ON ""DbSeasonInfo"" (""SeasonId"");");

            // Add SegmentIndex column to DbSegment if it doesn't exist
            migrationBuilder.Sql(
                @"CREATE TABLE IF NOT EXISTS ""DbSegment_new"" (
                    ""ItemId"" TEXT NOT NULL,
                    ""Type"" INTEGER NOT NULL,
                    ""SegmentIndex"" INTEGER NOT NULL DEFAULT 0,
                    ""Start"" REAL NOT NULL DEFAULT 0.0,
                    ""End"" REAL NOT NULL DEFAULT 0.0,
                    PRIMARY KEY (""ItemId"", ""Type"", ""SegmentIndex"")
                );

                INSERT OR IGNORE INTO ""DbSegment_new"" (""ItemId"", ""Type"", ""SegmentIndex"", ""Start"", ""End"")
                SELECT ""ItemId"", ""Type"", 0, ""Start"", ""End"" FROM ""DbSegment"";

                DROP TABLE IF EXISTS ""DbSegment"";

                ALTER TABLE ""DbSegment_new"" RENAME TO ""DbSegment"";

                CREATE INDEX IF NOT EXISTS ""IX_DbSegment_ItemId"" ON ""DbSegment"" (""ItemId"");");

            // Create DbSegmentOutbox table
            migrationBuilder.CreateTable(
                name: "DbSegmentOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    SegmentIndex = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Operation = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbSegmentOutbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DbSegmentOutbox_ItemId",
                table: "DbSegmentOutbox",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_DbSegmentOutbox_ItemId_Type_SegmentIndex",
                table: "DbSegmentOutbox",
                columns: _outboxIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DbSegmentOutbox");

            // Note: EpisodeIds column cannot be restored as data was lost
        }
    }
}
