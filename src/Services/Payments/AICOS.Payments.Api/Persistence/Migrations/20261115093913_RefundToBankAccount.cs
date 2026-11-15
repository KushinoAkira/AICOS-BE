using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Payments.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RefundToBankAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bank_account_holder",
                schema: "payment",
                table: "payment_refunds",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bank_account_number",
                schema: "payment",
                table: "payment_refunds",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bank_name",
                schema: "payment",
                table: "payment_refunds",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bank_account_holder",
                schema: "payment",
                table: "payment_refunds");

            migrationBuilder.DropColumn(
                name: "bank_account_number",
                schema: "payment",
                table: "payment_refunds");

            migrationBuilder.DropColumn(
                name: "bank_name",
                schema: "payment",
                table: "payment_refunds");
        }
    }
}
