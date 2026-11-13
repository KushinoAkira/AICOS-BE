using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.WorkforceScheduling.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AttendanceManagerStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "manager_note",
                schema: "workforce",
                table: "employee_shifts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "manager_status",
                schema: "workforce",
                table: "employee_shifts",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "manager_note",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.DropColumn(
                name: "manager_status",
                schema: "workforce",
                table: "employee_shifts");
        }
    }
}
