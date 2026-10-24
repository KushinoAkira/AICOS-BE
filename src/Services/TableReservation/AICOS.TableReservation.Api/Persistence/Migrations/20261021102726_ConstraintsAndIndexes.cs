using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.TableReservation.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConstraintsAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_dining_tables_capacity_positive",
                schema: "table_reservation",
                table: "dining_tables",
                sql: "capacity > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_dining_tables_capacity_positive",
                schema: "table_reservation",
                table: "dining_tables");
        }
    }
}
