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
            migrationBuilder.DropPrimaryKey(
                name: "PK_DbSegment",
                table: "DbSegment");

            migrationBuilder.DropIndex(
                name: "IX_DbSegment_ItemId",
                table: "DbSegment");

            migrationBuilder.DropColumn(
                name: "EpisodeIds",
                table: "DbSeasonInfo");

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "DbSegment",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFirstAppearance",
                table: "DbSegment",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SeasonId",
                table: "DbSegment",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_DbSegment",
                table: "DbSegment",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "DbSegmentOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Operation = table.Column<int>(type: "INTEGER", nullable: false),
                    SegmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
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
                name: "IX_DbSegment_ItemId_Type",
                table: "DbSegment",
                columns: ["ItemId", "Type"]);

            migrationBuilder.CreateIndex(
                name: "IX_DbSegment_SeasonId_Type",
                table: "DbSegment",
                columns: ["SeasonId", "Type"]);

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
                name: "IX_DbSegment_SeasonId_Type",
                table: "DbSegment");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "DbSegment");

            migrationBuilder.DropColumn(
                name: "IsFirstAppearance",
                table: "DbSegment");

            migrationBuilder.DropColumn(
                name: "SeasonId",
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
