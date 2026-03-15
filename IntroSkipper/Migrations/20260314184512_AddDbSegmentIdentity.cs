using System;
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
            migrationBuilder.CreateTable(
                name: "DbSegment_Temp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Start = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.0),
                    End = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbSegment_Temp", x => x.Id);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO DbSegment_Temp (ItemId, Type, Start, End)
                SELECT ItemId, Type, Start, End
                FROM DbSegment;
                """);

            migrationBuilder.DropTable(
                name: "DbSegment");

            migrationBuilder.RenameTable(
                name: "DbSegment_Temp",
                newName: "DbSegment");

            migrationBuilder.CreateIndex(
                name: "IX_DbSegment_ItemId",
                table: "DbSegment",
                column: "ItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DbSegment_Temp",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Start = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.0),
                    End = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbSegment_Temp", x => new { x.ItemId, x.Type });
                });

            migrationBuilder.Sql(
                """
                INSERT INTO DbSegment_Temp (ItemId, Type, Start, End)
                SELECT ItemId, Type, Start, End
                FROM DbSegment
                WHERE Id IN (
                    SELECT MIN(Id)
                    FROM DbSegment
                    GROUP BY ItemId, Type
                );
                """);

            migrationBuilder.DropTable(
                name: "DbSegment");

            migrationBuilder.RenameTable(
                name: "DbSegment_Temp",
                newName: "DbSegment");

            migrationBuilder.CreateIndex(
                name: "IX_DbSegment_ItemId",
                table: "DbSegment",
                column: "ItemId");
        }
    }
}
