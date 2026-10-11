// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations;

/// <inheritdoc />
public partial class AddSeasonSubtitleDetectionOverrides : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "SubtitleRecapDetection",
            table: "SeasonAnalysisOverrides",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "SubtitlePreviewDetection",
            table: "SeasonAnalysisOverrides",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SubtitleRecapDetection",
            table: "SeasonAnalysisOverrides");

        migrationBuilder.DropColumn(
            name: "SubtitlePreviewDetection",
            table: "SeasonAnalysisOverrides");
    }
}
