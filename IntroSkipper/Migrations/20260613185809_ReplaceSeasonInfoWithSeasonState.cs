using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceSeasonInfoWithSeasonState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DbSeasonState",
                columns: table => new
                {
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    EpisodeIds = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigHash = table.Column<string>(type: "TEXT", nullable: false, defaultValue: string.Empty),
                    SettledReanalysisEpisodeIds = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    LastSettledReanalysisUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbSeasonState", x => new { x.SeasonId, x.Type });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DbSeasonState_SeasonId",
                table: "DbSeasonState",
                column: "SeasonId");

            migrationBuilder.Sql(
                """
                INSERT INTO "DbSeasonState" ("SeasonId", "Type", "Action", "EpisodeIds", "ConfigHash", "SettledReanalysisEpisodeIds")
                SELECT "SeasonId", "Type", "Action", "EpisodeIds", COALESCE("ConfigHash", ''), '[]'
                FROM "DbSeasonInfo"
                """);

            migrationBuilder.DropTable(
                name: "DbSeasonInfo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DbSeasonInfo",
                columns: table => new
                {
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ConfigHash = table.Column<string>(type: "TEXT", nullable: false, defaultValue: string.Empty),
                    EpisodeIds = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbSeasonInfo", x => new { x.SeasonId, x.Type });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DbSeasonInfo_SeasonId",
                table: "DbSeasonInfo",
                column: "SeasonId");
            migrationBuilder.Sql(
                """
                INSERT INTO "DbSeasonInfo" ("SeasonId", "Type", "Action", "ConfigHash", "EpisodeIds")
                SELECT "SeasonId", "Type", "Action", COALESCE("ConfigHash", ''), "EpisodeIds"
                FROM "DbSeasonState"
                """);

            migrationBuilder.DropTable(
                name: "DbSeasonState");
        }
    }
}
