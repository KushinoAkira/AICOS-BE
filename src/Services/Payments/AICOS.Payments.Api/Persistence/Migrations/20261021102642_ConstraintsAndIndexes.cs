using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Payments.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConstraintsAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_amount_positive",
                schema: "payment",
                table: "payments",
                sql: "amount > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_refunded_range",
                schema: "payment",
                table: "payments",
                sql: "refunded_amount >= 0 AND refunded_amount <= amount");

            migrationBuilder.CreateIndex(
                name: "ix_payment_webhook_events_payment_id",
                schema: "payment",
                table: "payment_webhook_events",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_tenant_id_order_id",
                schema: "payment",
                table: "payment_refunds",
                columns: new[] { "tenant_id", "order_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_refunds_amount_positive",
                schema: "payment",
                table: "payment_refunds",
                sql: "amount > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cancellation_policies_minutes",
                schema: "payment",
                table: "cancellation_policies",
                sql: "no_show_after_minutes BETWEEN 5 AND 240 AND unpaid_order_timeout_minutes BETWEEN 5 AND 120");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cancellation_policies_refund_percent",
                schema: "payment",
                table: "cancellation_policies",
                sql: "cancel_refund_percent BETWEEN 0 AND 100 AND no_show_refund_percent BETWEEN 0 AND 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_amount_positive",
                schema: "payment",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_refunded_range",
                schema: "payment",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "ix_payment_webhook_events_payment_id",
                schema: "payment",
                table: "payment_webhook_events");

            migrationBuilder.DropIndex(
                name: "ix_payment_refunds_tenant_id_order_id",
                schema: "payment",
                table: "payment_refunds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_refunds_amount_positive",
                schema: "payment",
                table: "payment_refunds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cancellation_policies_minutes",
                schema: "payment",
                table: "cancellation_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cancellation_policies_refund_percent",
                schema: "payment",
                table: "cancellation_policies");
        }
    }
}
