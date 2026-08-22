using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AnalyzedItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalyzedItems",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigHash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyzedItems", x => new { x.ItemId, x.Type });
                });

            // Carry the per-season analyzed-episode lists over as one record per
            // (episode, mode) under the season's hash, so the upgrade does not re-scan
            // the library. Guid columns are uppercase TEXT; upper() keeps the copied ids
            // in that format whatever casing the JSON writer used.
            migrationBuilder.Sql(
                """
                INSERT OR IGNORE INTO "AnalyzedItems" ("ItemId", "Type", "ConfigHash")
                SELECT upper(j.value), s."Type", s."ConfigHash"
                FROM "SeasonStates" AS s, json_each(s."EpisodeIds") AS j
                """);

            migrationBuilder.DropColumn(
                name: "ConfigHash",
                table: "SeasonStates");

            migrationBuilder.DropColumn(
                name: "EpisodeIds",
                table: "SeasonStates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The records are not folded back into EpisodeIds: after a downgrade every
            // item is NotAnalyzed again and the next scan rebuilds the lists.
            migrationBuilder.DropTable(
                name: "AnalyzedItems");

            migrationBuilder.AddColumn<string>(
                name: "ConfigHash",
                table: "SeasonStates",
                type: "TEXT",
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.AddColumn<string>(
                name: "EpisodeIds",
                table: "SeasonStates",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }
    }
}
