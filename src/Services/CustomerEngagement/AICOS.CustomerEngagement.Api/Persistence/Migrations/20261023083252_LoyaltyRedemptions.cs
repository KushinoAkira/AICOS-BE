using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.CustomerEngagement.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LoyaltyRedemptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_loyalty_transactions_order_id",
                schema: "customer",
                table: "loyalty_transactions");

            migrationBuilder.AddColumn<int>(
                name: "version",
                schema: "customer",
                table: "customers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_transactions_order_id_type",
                schema: "customer",
                table: "loyalty_transactions",
                columns: new[] { "order_id", "type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_loyalty_transactions_order_id_type",
                schema: "customer",
                table: "loyalty_transactions");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "customer",
                table: "customers");

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_transactions_order_id",
                schema: "customer",
                table: "loyalty_transactions",
                column: "order_id",
                unique: true);
        }
    }
}
