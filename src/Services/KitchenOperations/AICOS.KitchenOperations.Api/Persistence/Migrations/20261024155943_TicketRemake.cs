using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.KitchenOperations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketRemake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_remake_at_utc",
                schema: "kitchen",
                table: "kitchen_tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_remake_reason",
                schema: "kitchen",
                table: "kitchen_tickets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "remake_count",
                schema: "kitchen",
                table: "kitchen_tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_remake_at_utc",
                schema: "kitchen",
                table: "kitchen_tickets");

            migrationBuilder.DropColumn(
                name: "last_remake_reason",
                schema: "kitchen",
                table: "kitchen_tickets");

            migrationBuilder.DropColumn(
                name: "remake_count",
                schema: "kitchen",
                table: "kitchen_tickets");
        }
    }
}
