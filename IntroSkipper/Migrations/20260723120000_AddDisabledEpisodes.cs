using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddDisabledEpisodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DbDisabledEpisode",
                columns: table => new
                {
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbDisabledEpisode", x => new { x.SeasonId, x.EpisodeId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DbDisabledEpisode_EpisodeId",
                table: "DbDisabledEpisode",
                column: "EpisodeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DbDisabledEpisode");
        }
    }
}
