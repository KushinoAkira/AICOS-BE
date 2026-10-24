using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.CustomerEngagement.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConstraintsAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_vouchers_issued_to_customer_id",
                schema: "customer",
                table: "vouchers",
                column: "issued_to_customer_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_vouchers_min_order_non_negative",
                schema: "customer",
                table: "vouchers",
                sql: "min_order_amount IS NULL OR min_order_amount >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_vouchers_value_positive",
                schema: "customer",
                table: "vouchers",
                sql: "value > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loyalty_rules_rates_positive",
                schema: "customer",
                table: "loyalty_rules",
                sql: "amount_per_point > 0 AND redeem_value_per_point > 0 AND min_points_to_redeem >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loyalty_rules_tier_order",
                schema: "customer",
                table: "loyalty_rules",
                sql: "silver_threshold >= 1 AND gold_threshold > silver_threshold");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customers_points_non_negative",
                schema: "customer",
                table: "customers",
                sql: "points_balance >= 0 AND lifetime_points >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customers_sugar_percent_range",
                schema: "customer",
                table: "customers",
                sql: "preferred_sugar_percent IS NULL OR preferred_sugar_percent BETWEEN 0 AND 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_vouchers_issued_to_customer_id",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_vouchers_min_order_non_negative",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_vouchers_value_positive",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loyalty_rules_rates_positive",
                schema: "customer",
                table: "loyalty_rules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loyalty_rules_tier_order",
                schema: "customer",
                table: "loyalty_rules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customers_points_non_negative",
                schema: "customer",
                table: "customers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customers_sugar_percent_range",
                schema: "customer",
                table: "customers");
        }
    }
}
