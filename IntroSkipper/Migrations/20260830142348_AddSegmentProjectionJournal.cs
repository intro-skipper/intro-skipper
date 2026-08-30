using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentProjectionJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectionExternalOperations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalSegmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpectedType = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionExternalOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectionQueue",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Failure = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionQueue", x => x.ItemId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionExternalOperations_ItemId",
                table: "ProjectionExternalOperations",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionQueue_NextAttemptAt",
                table: "ProjectionQueue",
                column: "NextAttemptAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectionExternalOperations");

            migrationBuilder.DropTable(
                name: "ProjectionQueue");
        }
    }
}
