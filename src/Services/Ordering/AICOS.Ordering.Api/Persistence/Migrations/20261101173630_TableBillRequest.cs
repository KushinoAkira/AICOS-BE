using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TableBillRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "bill_requested_at_utc",
                schema: "ordering",
                table: "table_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_requested_method",
                schema: "ordering",
                table: "table_sessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bill_requested_at_utc",
                schema: "ordering",
                table: "table_sessions");

            migrationBuilder.DropColumn(
                name: "bill_requested_method",
                schema: "ordering",
                table: "table_sessions");
        }
    }
}
