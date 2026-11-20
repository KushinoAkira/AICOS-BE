using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderBasketsAssociationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "association_rules",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pair_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    antecedent_product_ids = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    consequent_product_ids = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    antecedent_names = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    consequent_names = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    support = table.Column<double>(type: "double precision", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    lift = table.Column<double>(type: "double precision", nullable: false),
                    transaction_count = table.Column<int>(type: "integer", nullable: false),
                    mined_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    voucher_created = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_association_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "order_baskets",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_baskets", x => x.id);
                    table.CheckConstraint("ck_order_baskets_total", "total_amount >= 0");
                });

            migrationBuilder.CreateTable(
                name: "order_basket_items",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    basket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    line_amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_basket_items", x => x.id);
                    table.CheckConstraint("ck_order_basket_items_qty", "quantity > 0 AND line_amount >= 0");
                    table.ForeignKey(
                        name: "fk_order_basket_items_order_baskets_basket_id",
                        column: x => x.basket_id,
                        principalSchema: "ordering",
                        principalTable: "order_baskets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_association_rules_tenant_id_branch_id_pair_key",
                schema: "ordering",
                table: "association_rules",
                columns: new[] { "tenant_id", "branch_id", "pair_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_association_rules_tenant_id_mined_at_utc",
                schema: "ordering",
                table: "association_rules",
                columns: new[] { "tenant_id", "mined_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_order_basket_items_basket_id",
                schema: "ordering",
                table: "order_basket_items",
                column: "basket_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_basket_items_basket_id_product_id",
                schema: "ordering",
                table: "order_basket_items",
                columns: new[] { "basket_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_order_baskets_tenant_id_branch_id_paid_at_utc",
                schema: "ordering",
                table: "order_baskets",
                columns: new[] { "tenant_id", "branch_id", "paid_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_order_baskets_tenant_id_paid_at_utc",
                schema: "ordering",
                table: "order_baskets",
                columns: new[] { "tenant_id", "paid_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "association_rules",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "order_basket_items",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "order_baskets",
                schema: "ordering");
        }
    }
}
