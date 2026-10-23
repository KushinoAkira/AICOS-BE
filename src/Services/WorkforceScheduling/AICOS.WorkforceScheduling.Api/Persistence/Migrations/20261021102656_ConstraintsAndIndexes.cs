using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.WorkforceScheduling.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConstraintsAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_payroll_adjustments_amount_not_zero",
                schema: "workforce",
                table: "payroll_adjustments",
                sql: "amount <> 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_rules_overtime_range",
                schema: "workforce",
                table: "pay_rules",
                sql: "overtime_after_hours BETWEEN 1 AND 24 AND overtime_multiplier BETWEEN 1 AND 5");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_rules_rates_positive",
                schema: "workforce",
                table: "pay_rules",
                sql: "full_time_day_rate > 0 AND part_time_hour_rate > 0 AND replacement_hour_rate > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payroll_adjustments_amount_not_zero",
                schema: "workforce",
                table: "payroll_adjustments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pay_rules_overtime_range",
                schema: "workforce",
                table: "pay_rules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pay_rules_rates_positive",
                schema: "workforce",
                table: "pay_rules");
        }
    }
}
