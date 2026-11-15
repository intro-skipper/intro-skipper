// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations;

/// <inheritdoc />
[DbContext(typeof(IntroSkipperDbContext))]
[Migration("20261114100000_AddPreviewRecommendation")]
public partial class AddPreviewRecommendation : Migration
{
    /// <summary>Applies the recommendation column to season overrides.</summary>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "PreviewFromCreditsEndRecommended",
            table: "SeasonAnalysisOverrides",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <summary>Removes the recommendation column from season overrides.</summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PreviewFromCreditsEndRecommended",
            table: "SeasonAnalysisOverrides");
    }
}
