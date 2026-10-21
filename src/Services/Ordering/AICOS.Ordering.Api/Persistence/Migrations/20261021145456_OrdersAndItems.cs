using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrdersAndItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "orders",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    table_id = table.Column<Guid>(type: "uuid", nullable: true),
                    table_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    delivery_address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payment_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payment_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    payment_provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ready_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered_by = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    promotion_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    discount_amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    refunded_amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    refund_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    exception_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_printed = table.Column<bool>(type: "boolean", nullable: false),
                    printed_count = table.Column<int>(type: "integer", nullable: false),
                    last_printed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_amounts", "discount_amount >= 0 AND refunded_amount >= 0");
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_items", x => x.id);
                    table.CheckConstraint("ck_order_items_quantity_price", "quantity > 0 AND unit_price >= 0");
                    table.ForeignKey(
                        name: "fk_order_items_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "ordering",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_items_order_id",
                schema: "ordering",
                table: "order_items",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_customer_id",
                schema: "ordering",
                table: "orders",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_payment_id",
                schema: "ordering",
                table: "orders",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_branch_id_created_at_utc",
                schema: "ordering",
                table: "orders",
                columns: new[] { "tenant_id", "branch_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_branch_id_status",
                schema: "ordering",
                table: "orders",
                columns: new[] { "tenant_id", "branch_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_order_code",
                schema: "ordering",
                table: "orders",
                columns: new[] { "tenant_id", "order_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_items",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "ordering");
        }
    }
}
