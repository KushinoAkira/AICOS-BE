using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConstraintsAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_pickup_slot_settings_ranges",
                schema: "ordering",
                table: "pickup_slot_settings",
                sql: "slot_minutes BETWEEN 5 AND 120 AND capacity_per_slot BETWEEN 1 AND 500 AND cutoff_minutes BETWEEN 0 AND 240 AND advance_days BETWEEN 0 AND 14");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_pickup_slot_settings_ranges",
                schema: "ordering",
                table: "pickup_slot_settings");
        }
    }
}
