using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.KitchenOperations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KitchenEarlyPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "early_payments",
                schema: "kitchen",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_early_payments", x => new { x.tenant_id, x.order_id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "early_payments",
                schema: "kitchen");
        }
    }
}
