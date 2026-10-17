using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteAnalysisBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteAnalysisMetadata",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CanonicalJson = table.Column<string>(type: "TEXT", nullable: true),
                    DetectorId = table.Column<string>(type: "TEXT", nullable: true),
                    ProfileHash = table.Column<string>(type: "TEXT", nullable: true),
                    UpstreamCommit = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteAnalysisMetadata", x => new { x.ItemId, x.Mode });
                });

            migrationBuilder.CreateTable(
                name: "RemoteExportContexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteExportContexts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteImportReceipts",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnalysisId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadHash = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    OriginalCanonicalJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteImportReceipts", x => new { x.AnalysisId, x.ItemId, x.Mode });
                });

            migrationBuilder.CreateTable(
                name: "RemoteJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotHash = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerId = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseTokenHash = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteJobs_State_LeaseExpiresUtc",
                table: "RemoteJobs",
                columns: new[] { "State", "LeaseExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteAnalysisMetadata");

            migrationBuilder.DropTable(
                name: "RemoteExportContexts");

            migrationBuilder.DropTable(
                name: "RemoteImportReceipts");

            migrationBuilder.DropTable(
                name: "RemoteJobs");
        }
    }
}
