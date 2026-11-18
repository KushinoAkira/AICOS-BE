using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.WorkforceScheduling.Api.Persistence.Migrations
{
    /// <summary>Branch managers get a monthly salary; a payroll period is either the staff of a branch or the managers of the brand.</summary>
    public partial class ManagerPayroll : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "monthly_salary",
                schema: "workforce",
                table: "employees",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "workforce",
                table: "payroll_periods",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Staff");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "monthly_salary",
                schema: "workforce",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "workforce",
                table: "payroll_periods");
        }
    }
}
