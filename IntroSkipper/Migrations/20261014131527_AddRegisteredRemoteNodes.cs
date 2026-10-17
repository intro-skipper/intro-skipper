using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddRegisteredRemoteNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NodeId",
                table: "RemoteJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NodeRevision",
                table: "RemoteJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RemoteNodes",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    ScopeJson = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteNodes", x => x.NodeId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteJobs_NodeId_NodeRevision_State_LeaseExpiresUtc",
                table: "RemoteJobs",
                columns: new[] { "NodeId", "NodeRevision", "State", "LeaseExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteNodes");

            migrationBuilder.DropIndex(
                name: "IX_RemoteJobs_NodeId_NodeRevision_State_LeaseExpiresUtc",
                table: "RemoteJobs");

            migrationBuilder.DropColumn(
                name: "NodeId",
                table: "RemoteJobs");

            migrationBuilder.DropColumn(
                name: "NodeRevision",
                table: "RemoteJobs");
        }
    }
}
