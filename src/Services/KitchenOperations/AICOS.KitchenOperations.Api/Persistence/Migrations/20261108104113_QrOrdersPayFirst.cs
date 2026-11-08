using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.KitchenOperations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QrOrdersPayFirst : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "pay_first",
                schema: "kitchen",
                table: "kitchen_tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pay_first",
                schema: "kitchen",
                table: "kitchen_tickets");
        }
    }
}
