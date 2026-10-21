using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.MenuCatalog.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConstraintsAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_products_base_price_positive",
                schema: "menu",
                table: "products",
                sql: "base_price > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_branch_products_price_override_positive",
                schema: "menu",
                table: "branch_products",
                sql: "price_override IS NULL OR price_override > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_products_base_price_positive",
                schema: "menu",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_branch_products_price_override_positive",
                schema: "menu",
                table: "branch_products");
        }
    }
}
