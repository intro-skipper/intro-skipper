// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations;

/// <inheritdoc />
public partial class AddShortcutAnalysisIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<double>(
            name: "Duration",
            table: "AnalyzedItems",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ShortcutPath",
            table: "AnalyzedItems",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Duration",
            table: "AnalyzedItems");

        migrationBuilder.DropColumn(
            name: "ShortcutPath",
            table: "AnalyzedItems");
    }
}
