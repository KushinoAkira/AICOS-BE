using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.MenuCatalog.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantMenuAndBranchProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The old menu belonged to single branches (one copy per branch, same names), which cannot be merged into one
            // brand menu automatically. It was demo data of the previous topic, so it is cleared; publish the new brand menu afterwards.
            migrationBuilder.Sql("DELETE FROM menu.products; DELETE FROM menu.categories;");
            migrationBuilder.DropTable(
                name: "promotion_products",
                schema: "menu");

            migrationBuilder.DropTable(
                name: "promotions",
                schema: "menu");

            migrationBuilder.DropColumn(
                name: "is_available",
                schema: "menu",
                table: "products");

            migrationBuilder.RenameColumn(
                name: "branch_id",
                schema: "menu",
                table: "products",
                newName: "tenant_id");

            migrationBuilder.RenameIndex(
                name: "ix_products_branch_id_name",
                schema: "menu",
                table: "products",
                newName: "ix_products_tenant_id_name");

            migrationBuilder.RenameColumn(
                name: "branch_id",
                schema: "menu",
                table: "categories",
                newName: "tenant_id");

            migrationBuilder.RenameIndex(
                name: "ix_categories_branch_id_name",
                schema: "menu",
                table: "categories",
                newName: "ix_categories_tenant_id_name");

            migrationBuilder.AddColumn<Guid>(
                name: "requires_option_id",
                schema: "menu",
                table: "product_options",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "menu",
                table: "option_groups",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "max_selections",
                schema: "menu",
                table: "option_groups",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "branch_products",
                schema: "menu",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    is_available = table.Column<bool>(type: "boolean", nullable: false),
                    price_override = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_branch_products", x => x.id);
                    table.ForeignKey(
                        name: "fk_branch_products_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "menu",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_branch_products_branch_id_product_id",
                schema: "menu",
                table: "branch_products",
                columns: new[] { "branch_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_branch_products_product_id",
                schema: "menu",
                table: "branch_products",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_branch_products_tenant_id_branch_id",
                schema: "menu",
                table: "branch_products",
                columns: new[] { "tenant_id", "branch_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "branch_products",
                schema: "menu");

            migrationBuilder.DropColumn(
                name: "requires_option_id",
                schema: "menu",
                table: "product_options");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "menu",
                table: "option_groups");

            migrationBuilder.DropColumn(
                name: "max_selections",
                schema: "menu",
                table: "option_groups");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "menu",
                table: "products",
                newName: "branch_id");

            migrationBuilder.RenameIndex(
                name: "ix_products_tenant_id_name",
                schema: "menu",
                table: "products",
                newName: "ix_products_branch_id_name");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "menu",
                table: "categories",
                newName: "branch_id");

            migrationBuilder.RenameIndex(
                name: "ix_categories_tenant_id_name",
                schema: "menu",
                table: "categories",
                newName: "ix_categories_branch_id_name");

            migrationBuilder.AddColumn<bool>(
                name: "is_available",
                schema: "menu",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "promotions",
                schema: "menu",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    discount_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    discount_value = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    end_date_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    start_date_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "promotion_products",
                schema: "menu",
                columns: table => new
                {
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotion_products", x => new { x.promotion_id, x.product_id });
                    table.ForeignKey(
                        name: "fk_promotion_products_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "menu",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_promotion_products_promotions_promotion_id",
                        column: x => x.promotion_id,
                        principalSchema: "menu",
                        principalTable: "promotions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_promotion_products_product_id",
                schema: "menu",
                table: "promotion_products",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_promotions_branch_id_status",
                schema: "menu",
                table: "promotions",
                columns: new[] { "branch_id", "status" });
        }
    }
}
