using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PickupOrdersAndFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                schema: "ordering",
                table: "orders",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "payment_due_at_utc",
                schema: "ordering",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "pickup_slot_start_utc",
                schema: "ordering",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "order_feedbacks",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_feedbacks", x => x.id);
                    table.CheckConstraint("ck_order_feedbacks_rating", "rating BETWEEN 1 AND 5");
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_payment_due_at_utc",
                schema: "ordering",
                table: "orders",
                column: "payment_due_at_utc",
                filter: "payment_due_at_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_branch_id_pickup_slot_start_utc",
                schema: "ordering",
                table: "orders",
                columns: new[] { "tenant_id", "branch_id", "pickup_slot_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_customer_id_idempotency_key",
                schema: "ordering",
                table: "orders",
                columns: new[] { "tenant_id", "customer_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_order_feedbacks_customer_id",
                schema: "ordering",
                table: "order_feedbacks",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_feedbacks_order_id",
                schema: "ordering",
                table: "order_feedbacks",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_feedbacks_tenant_id_branch_id_created_at_utc",
                schema: "ordering",
                table: "order_feedbacks",
                columns: new[] { "tenant_id", "branch_id", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_feedbacks",
                schema: "ordering");

            migrationBuilder.DropIndex(
                name: "ix_orders_payment_due_at_utc",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_tenant_id_branch_id_pickup_slot_start_utc",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_tenant_id_customer_id_idempotency_key",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payment_due_at_utc",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "pickup_slot_start_utc",
                schema: "ordering",
                table: "orders");
        }
    }
}
