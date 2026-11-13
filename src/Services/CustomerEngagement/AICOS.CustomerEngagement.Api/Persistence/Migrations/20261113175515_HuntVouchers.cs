using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.CustomerEngagement.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HuntVouchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "campaign_id",
                schema: "customer",
                table: "vouchers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "count_orders_from_utc",
                schema: "customer",
                table: "vouchers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "required_order_amount",
                schema: "customer",
                table: "vouchers",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "required_orders",
                schema: "customer",
                table: "vouchers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "member_orders",
                schema: "customer",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_member_orders_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "customer",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vouchers_campaign_id_issued_to_customer_id",
                schema: "customer",
                table: "vouchers",
                columns: new[] { "campaign_id", "issued_to_customer_id" },
                unique: true,
                filter: "campaign_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_member_orders_customer_id_paid_at_utc",
                schema: "customer",
                table: "member_orders",
                columns: new[] { "customer_id", "paid_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_member_orders_order_id",
                schema: "customer",
                table: "member_orders",
                column: "order_id",
                unique: true);

            // Orders paid before this change: the points history kept them ("Order X: paid 30000"); a reversed one stays cancelled.
            migrationBuilder.Sql("""
                INSERT INTO customer.member_orders (id, tenant_id, customer_id, order_id, amount, paid_at_utc, is_cancelled, created_at_utc)
                SELECT gen_random_uuid(), c.tenant_id, t.customer_id, t.order_id, CAST(substring(t.note from 'paid ([0-9]+([.][0-9]+)?)') AS numeric(14,2)),
                       t.created_at_utc,
                       EXISTS (SELECT 1 FROM customer.loyalty_transactions r WHERE r.order_id = t.order_id AND r.type = 'Reverse'),
                       t.created_at_utc
                FROM customer.loyalty_transactions t
                JOIN customer.customers c ON c.id = t.customer_id
                WHERE t.type = 'Earn' AND t.order_id IS NOT NULL AND t.note ~ 'paid [0-9]+'
                ON CONFLICT (order_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "member_orders",
                schema: "customer");

            migrationBuilder.DropIndex(
                name: "ix_vouchers_campaign_id_issued_to_customer_id",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.DropColumn(
                name: "campaign_id",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.DropColumn(
                name: "count_orders_from_utc",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.DropColumn(
                name: "required_order_amount",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.DropColumn(
                name: "required_orders",
                schema: "customer",
                table: "vouchers");
        }
    }
}
