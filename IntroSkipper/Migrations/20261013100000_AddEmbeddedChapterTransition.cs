// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations;

/// <inheritdoc />
public partial class AddEmbeddedChapterTransition : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "EmbeddedChapterTransitions",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                AppliedEmbeddedChapters = table.Column<bool>(type: "INTEGER", nullable: false),
                PendingEmbeddedChapters = table.Column<bool>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EmbeddedChapterTransitions", x => x.Id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "EmbeddedChapterTransitions");
    }
}
