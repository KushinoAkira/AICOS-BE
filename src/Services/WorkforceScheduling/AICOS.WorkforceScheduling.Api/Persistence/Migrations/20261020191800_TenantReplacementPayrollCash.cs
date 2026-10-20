using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.WorkforceScheduling.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantReplacementPayrollCash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The previous topic paid staff a fixed salary or an hourly rate per person and let employees swap shifts. The new model has a
            // contract type (full-time by day, part-time by hour), brand pay rules and replacement shifts, and every row belongs to a tenant.
            // Old staff records cannot be mapped to that automatically, so they are cleared: create the staff profiles and shifts again.
            migrationBuilder.Sql(
                "DELETE FROM workforce.shift_swap_requests; DELETE FROM workforce.employee_shifts; DELETE FROM workforce.work_shifts; DELETE FROM workforce.employees;");

            migrationBuilder.DropTable(
                name: "shift_swap_requests",
                schema: "workforce");

            migrationBuilder.DropIndex(
                name: "ix_employees_branch_id",
                schema: "workforce",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "fixed_salary",
                schema: "workforce",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "hourly_rate",
                schema: "workforce",
                table: "employees");

            migrationBuilder.RenameColumn(
                name: "payment_type",
                schema: "workforce",
                table: "employees",
                newName: "contract_type");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "workforce",
                table: "work_shifts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "role",
                schema: "workforce",
                table: "employees",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "workforce",
                table: "employees",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AlterColumn<Guid>(
                name: "work_shift_id",
                schema: "workforce",
                table: "employee_shifts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTime>(
                name: "adjusted_check_in_at_utc",
                schema: "workforce",
                table: "employee_shifts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "adjusted_check_out_at_utc",
                schema: "workforce",
                table: "employee_shifts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_absent",
                schema: "workforce",
                table: "employee_shifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_replacement",
                schema: "workforce",
                table: "employee_shifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "replacement_shift_id",
                schema: "workforce",
                table: "employee_shifts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_note",
                schema: "workforce",
                table: "employee_shifts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_status",
                schema: "workforce",
                table: "employee_shifts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "reviewed_at_utc",
                schema: "workforce",
                table: "employee_shifts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reviewed_by",
                schema: "workforce",
                table: "employee_shifts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "workforce",
                table: "employee_shifts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "cash_closings",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    counted_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    difference = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    cashier_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    manager_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    confirmed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_closings", x => x.id);
                    table.ForeignKey(
                        name: "fk_cash_closings_shift_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalSchema: "workforce",
                        principalTable: "employee_shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_transactions",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_transactions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pay_rules",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_time_day_rate = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    part_time_hour_rate = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    replacement_hour_rate = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    overtime_after_hours = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    overtime_multiplier = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pay_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "replacement_shifts",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_shift_id = table.Column<Guid>(type: "uuid", nullable: true),
                    work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    original_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approved_employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    manager_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_replacement_shifts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "replacement_applications",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    replacement_shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_replacement_applications", x => x.id);
                    table.ForeignKey(
                        name: "fk_replacement_applications_employees_employee_id",
                        column: x => x.employee_id,
                        principalSchema: "workforce",
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_replacement_applications_replacement_shifts_replacement_shi",
                        column: x => x.replacement_shift_id,
                        principalSchema: "workforce",
                        principalTable: "replacement_shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_shifts_tenant_id",
                schema: "workforce",
                table: "work_shifts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_employees_tenant_id_branch_id",
                schema: "workforce",
                table: "employees",
                columns: new[] { "tenant_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_shifts_tenant_id",
                schema: "workforce",
                table: "employee_shifts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_cash_closings_assignment_id",
                schema: "workforce",
                table: "cash_closings",
                column: "assignment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cash_closings_tenant_id_branch_id_status",
                schema: "workforce",
                table: "cash_closings",
                columns: new[] { "tenant_id", "branch_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_transactions_cashier_user_id_occurred_at_utc",
                schema: "workforce",
                table: "cash_transactions",
                columns: new[] { "cashier_user_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_transactions_source_event_id",
                schema: "workforce",
                table: "cash_transactions",
                column: "source_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pay_rules_tenant_id",
                schema: "workforce",
                table: "pay_rules",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_replacement_applications_employee_id",
                schema: "workforce",
                table: "replacement_applications",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_replacement_applications_replacement_shift_id_employee_id",
                schema: "workforce",
                table: "replacement_applications",
                columns: new[] { "replacement_shift_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_shifts_branch_id_status",
                schema: "workforce",
                table: "replacement_shifts",
                columns: new[] { "branch_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_shifts_tenant_id",
                schema: "workforce",
                table: "replacement_shifts",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cash_closings",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "cash_transactions",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "pay_rules",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "replacement_applications",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "replacement_shifts",
                schema: "workforce");

            migrationBuilder.DropIndex(
                name: "ix_work_shifts_tenant_id",
                schema: "workforce",
                table: "work_shifts");

            migrationBuilder.DropIndex(
                name: "ix_employees_tenant_id_branch_id",
                schema: "workforce",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "ix_employee_shifts_tenant_id",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "workforce",
                table: "work_shifts");

            migrationBuilder.DropColumn(
                name: "role",
                schema: "workforce",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "workforce",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "adjusted_check_in_at_utc",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.DropColumn(
                name: "adjusted_check_out_at_utc",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.DropColumn(
                name: "is_absent",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.DropColumn(
                name: "is_replacement",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.DropColumn(
                name: "replacement_shift_id",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.DropColumn(
                name: "review_note",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.DropColumn(
                name: "review_status",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.DropColumn(
                name: "reviewed_at_utc",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.DropColumn(
                name: "reviewed_by",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "workforce",
                table: "employee_shifts");

            migrationBuilder.RenameColumn(
                name: "contract_type",
                schema: "workforce",
                table: "employees",
                newName: "payment_type");

            migrationBuilder.AddColumn<decimal>(
                name: "fixed_salary",
                schema: "workforce",
                table: "employees",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "hourly_rate",
                schema: "workforce",
                table: "employees",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "work_shift_id",
                schema: "workforce",
                table: "employee_shifts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "shift_swap_requests",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    manager_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    requester_employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shift_swap_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_shift_swap_requests_employee_shifts_assignment_id",
                        column: x => x.assignment_id,
                        principalSchema: "workforce",
                        principalTable: "employee_shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_employees_branch_id",
                schema: "workforce",
                table: "employees",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_shift_swap_requests_assignment_id",
                schema: "workforce",
                table: "shift_swap_requests",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "ix_shift_swap_requests_branch_id_status",
                schema: "workforce",
                table: "shift_swap_requests",
                columns: new[] { "branch_id", "status" });
        }
    }
}
