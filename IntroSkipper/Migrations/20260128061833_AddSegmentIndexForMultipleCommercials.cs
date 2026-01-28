using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Avoid constant arrays as arguments

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentIndexForMultipleCommercials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DbSegment",
                table: "DbSegment");

            migrationBuilder.AddColumn<int>(
                name: "SegmentIndex",
                table: "DbSegment",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_DbSegment",
                table: "DbSegment",
                columns: new[] { "ItemId", "Type", "SegmentIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DbSegment",
                table: "DbSegment");

            migrationBuilder.DropColumn(
                name: "SegmentIndex",
                table: "DbSegment");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DbSegment",
                table: "DbSegment",
                columns: new[] { "ItemId", "Type" });
        }
    }
}
