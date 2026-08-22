using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations
{
    /// <inheritdoc />
    public partial class SegmentRangeCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite adds the constraint by rebuilding the table; a degenerate row (never
            // written by the facade, but possible through raw SQL) would abort that rebuild
            // and with it every later start, so such rows are dropped first.
            migrationBuilder.Sql(
                """
                DELETE FROM "Segments" WHERE "EndTicks" <= "StartTicks" OR "StartTicks" < 0
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Segments_Range",
                table: "Segments",
                sql: "\"EndTicks\" > \"StartTicks\" AND \"StartTicks\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Segments_Range",
                table: "Segments");
        }
    }
}
