// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations;

/// <inheritdoc />
[DbContext(typeof(IntroSkipper.Db.IntroSkipperDbContext))]
[Migration("20261022120000_AddSeasonSubtitleCreditsDetectionOverride")]
public partial class AddSeasonSubtitleCreditsDetectionOverride : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "SubtitleCreditsDetection",
            table: "SeasonAnalysisOverrides",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SubtitleCreditsDetection",
            table: "SeasonAnalysisOverrides");
    }
}
