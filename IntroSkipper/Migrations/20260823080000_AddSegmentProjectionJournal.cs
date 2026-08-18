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
                name: "ProjectionAttempts",
                columns: table => new
                {
                    ChangeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Failure = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionAttempts", x => new { x.ChangeId, x.ItemId });
                });

            migrationBuilder.CreateTable(
                name: "ProjectionExternalOperations",
                columns: table => new
                {
                    ChangeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalSegmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpectedType = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionExternalOperations", x => new { x.ChangeId, x.ItemId, x.Position });
                });

            migrationBuilder.CreateTable(
                name: "ProjectionHeads",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastAcceptedSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAppliedSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionHeads", x => x.ItemId);
                });

            migrationBuilder.CreateTable(
                name: "ProjectionPlans",
                columns: table => new
                {
                    ChangeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionPlans", x => new { x.ChangeId, x.ItemId });
                });

            migrationBuilder.CreateTable(
                name: "ProjectionPlanSegments",
                columns: table => new
                {
                    ChangeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    SegmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    StartTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionPlanSegments", x => new { x.ChangeId, x.ItemId, x.Position });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionExternalOperations_ItemId_ExternalSegmentId",
                table: "ProjectionExternalOperations",
                columns: new[] { "ItemId", "ExternalSegmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionPlans_ItemId_Sequence",
                table: "ProjectionPlans",
                columns: new[] { "ItemId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectionAttempts");

            migrationBuilder.DropTable(
                name: "ProjectionExternalOperations");

            migrationBuilder.DropTable(
                name: "ProjectionHeads");

            migrationBuilder.DropTable(
                name: "ProjectionPlans");

            migrationBuilder.DropTable(
                name: "ProjectionPlanSegments");
        }
    }
}
