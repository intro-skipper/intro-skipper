using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DbSeasonInfo",
                columns: table => new
                {
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbSeasonInfo", x => new { x.SeasonId, x.Type });
                });

            migrationBuilder.CreateTable(
                name: "DbSegment",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Start = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.0),
                    End = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbSegment", x => new { x.ItemId, x.Type });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DbSeasonInfo_SeasonId",
                table: "DbSeasonInfo",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_DbSegment_ItemId",
                table: "DbSegment",
                column: "ItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DbSeasonInfo");

            migrationBuilder.DropTable(
                name: "DbSegment");
        }
    }
}
