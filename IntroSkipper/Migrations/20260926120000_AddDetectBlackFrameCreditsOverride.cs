// SPDX-FileCopyrightText: 2026 Triktron
// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations;

/// <inheritdoc />
public partial class AddDetectBlackFrameCreditsOverride : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "DetectBlackFrameCredits",
            table: "SeasonAnalysisOverrides",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DetectBlackFrameCredits",
            table: "SeasonAnalysisOverrides");
    }
}
