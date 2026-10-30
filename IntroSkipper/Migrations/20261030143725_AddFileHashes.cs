// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddFileHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileHashes",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    FileVersion = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileHashes", x => x.ItemId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileHashes_Hash",
                table: "FileHashes",
                column: "Hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileHashes");
        }
    }
}
