using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.CustomerEngagement.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerProfilePerBrand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_customers_tenant_id",
                schema: "customer",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "ix_customers_user_id",
                schema: "customer",
                table: "customers");

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_user_id",
                schema: "customer",
                table: "customers",
                columns: new[] { "tenant_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customers_user_id",
                schema: "customer",
                table: "customers",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_customers_tenant_id_user_id",
                schema: "customer",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "ix_customers_user_id",
                schema: "customer",
                table: "customers");

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id",
                schema: "customer",
                table: "customers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_customers_user_id",
                schema: "customer",
                table: "customers",
                column: "user_id",
                unique: true);
        }
    }
}
