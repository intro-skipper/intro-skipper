using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddDisabledItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DisabledItems",
                columns: table => new
                {
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DisabledItems", x => new { x.SeasonId, x.ItemId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DisabledItems_ItemId",
                table: "DisabledItems",
                column: "ItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DisabledItems");
        }
    }
}
