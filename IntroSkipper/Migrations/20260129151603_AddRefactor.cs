using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddRefactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EpisodeIds",
                table: "DbSeasonInfo");

            migrationBuilder.Sql(@"
ALTER TABLE DbSegment RENAME TO DbSegment_Old;

CREATE TABLE DbSegment (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    ItemId TEXT NOT NULL,
    SeasonId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
    Start REAL NOT NULL DEFAULT 0.0,
    End REAL NOT NULL DEFAULT 0.0,
    Type INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    IsFirstAppearance INTEGER NOT NULL DEFAULT 0
);

INSERT INTO DbSegment (Id, ItemId, SeasonId, Start, End, Type, CreatedAt, UpdatedAt, IsFirstAppearance)
SELECT rowid,
       ItemId,
       '00000000-0000-0000-0000-000000000000',
       Start,
       End,
       Type,
       CURRENT_TIMESTAMP,
       CURRENT_TIMESTAMP,
       0
FROM DbSegment_Old;

DROP TABLE DbSegment_Old;

CREATE INDEX IX_DbSegment_ItemId_Type ON DbSegment (ItemId, Type);
CREATE INDEX IX_DbSegment_SeasonId ON DbSegment (SeasonId);
");

            migrationBuilder.CreateTable(
                name: "DbSegmentOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Operation = table.Column<int>(type: "INTEGER", nullable: false),
                    SegmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValue: DateTime.UtcNow),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbSegmentOutbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DbSegmentOutbox_ItemId_ProcessedAt",
                table: "DbSegmentOutbox",
                columns: ["ItemId", "ProcessedAt"]);

            migrationBuilder.CreateIndex(
                name: "IX_DbSegmentOutbox_Pending",
                table: "DbSegmentOutbox",
                columns: ["ProcessedAt", "ClaimedBy", "RetryCount", "CreatedAt"]);

            migrationBuilder.CreateIndex(
                name: "IX_DbSegmentOutbox_ProcessedAt",
                table: "DbSegmentOutbox",
                column: "ProcessedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DbSegmentOutbox");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DbSegment",
                table: "DbSegment");

            migrationBuilder.DropIndex(
                name: "IX_DbSegment_ItemId_Type",
                table: "DbSegment");

            migrationBuilder.DropIndex(
                name: "IX_DbSegment_SeasonId",
                table: "DbSegment");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "DbSegment");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "DbSegment");

            migrationBuilder.DropColumn(
                name: "IsFirstAppearance",
                table: "DbSegment");

            migrationBuilder.DropColumn(
                name: "SeasonId",
                table: "DbSegment");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "DbSegment");

            migrationBuilder.AddColumn<string>(
                name: "EpisodeIds",
                table: "DbSeasonInfo",
                type: "TEXT",
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.AddPrimaryKey(
                name: "PK_DbSegment",
                table: "DbSegment",
                columns: ["ItemId", "Type"]);

            migrationBuilder.CreateIndex(
                name: "IX_DbSegment_ItemId",
                table: "DbSegment",
                column: "ItemId");
        }
    }
}
