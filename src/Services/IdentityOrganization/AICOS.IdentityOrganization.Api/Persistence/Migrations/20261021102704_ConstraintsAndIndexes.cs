using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.IdentityOrganization.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConstraintsAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_subscription_plans_fee_non_negative",
                schema: "identity",
                table: "subscription_plans",
                sql: "fee >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscription_plans_limits_positive",
                schema: "identity",
                table: "subscription_plans",
                sql: "(max_branches IS NULL OR max_branches > 0) AND (max_staff_accounts IS NULL OR max_staff_accounts > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscription_payments_amount_positive",
                schema: "identity",
                table: "subscription_payments",
                sql: "amount > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_subscription_plans_fee_non_negative",
                schema: "identity",
                table: "subscription_plans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_subscription_plans_limits_positive",
                schema: "identity",
                table: "subscription_plans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_subscription_payments_amount_positive",
                schema: "identity",
                table: "subscription_payments");
        }
    }
}
