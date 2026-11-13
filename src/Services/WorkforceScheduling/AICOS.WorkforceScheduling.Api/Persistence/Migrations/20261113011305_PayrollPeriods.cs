using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.WorkforceScheduling.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bank_account_name",
                schema: "workforce",
                table: "employees",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bank_account_number",
                schema: "workforce",
                table: "employees",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bank_name",
                schema: "workforce",
                table: "employees",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payroll_periods",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from = table.Column<DateOnly>(type: "date", nullable: false),
                    to = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    finalized_by = table.Column<Guid>(type: "uuid", nullable: true),
                    finalized_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reopened_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reopened_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reopen_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payroll_periods", x => x.id);
                    table.CheckConstraint("ck_payroll_periods_range", "\"from\" <= \"to\"");
                });

            migrationBuilder.CreateTable(
                name: "payroll_period_lines",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    contract_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    worked_days = table.Column<int>(type: "integer", nullable: false),
                    regular_hours = table.Column<double>(type: "double precision", nullable: false),
                    overtime_hours = table.Column<double>(type: "double precision", nullable: false),
                    replacement_hours = table.Column<double>(type: "double precision", nullable: false),
                    base_pay = table.Column<decimal>(type: "numeric(14,0)", precision: 14, scale: 0, nullable: false),
                    overtime_pay = table.Column<decimal>(type: "numeric(14,0)", precision: 14, scale: 0, nullable: false),
                    replacement_pay = table.Column<decimal>(type: "numeric(14,0)", precision: 14, scale: 0, nullable: false),
                    adjustments = table.Column<decimal>(type: "numeric(14,0)", precision: 14, scale: 0, nullable: false),
                    gross_pay = table.Column<decimal>(type: "numeric(14,0)", precision: 14, scale: 0, nullable: false),
                    net_pay = table.Column<decimal>(type: "numeric(14,0)", precision: 14, scale: 0, nullable: false),
                    bank_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    bank_account_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    bank_account_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    paid_by = table.Column<Guid>(type: "uuid", nullable: true),
                    payout_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    payout_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payroll_period_lines", x => x.id);
                    table.CheckConstraint("ck_payroll_period_lines_net", "net_pay >= 0");
                    table.ForeignKey(
                        name: "fk_payroll_period_lines_payroll_periods_period_id",
                        column: x => x.period_id,
                        principalSchema: "workforce",
                        principalTable: "payroll_periods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_period_lines_employee_id",
                schema: "workforce",
                table: "payroll_period_lines",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_payroll_period_lines_period_id_employee_id",
                schema: "workforce",
                table: "payroll_period_lines",
                columns: new[] { "period_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payroll_periods_tenant_id_branch_id_from_to",
                schema: "workforce",
                table: "payroll_periods",
                columns: new[] { "tenant_id", "branch_id", "from", "to" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payroll_period_lines",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "payroll_periods",
                schema: "workforce");

            migrationBuilder.DropColumn(
                name: "bank_account_name",
                schema: "workforce",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "bank_account_number",
                schema: "workforce",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "bank_name",
                schema: "workforce",
                table: "employees");
        }
    }
}
