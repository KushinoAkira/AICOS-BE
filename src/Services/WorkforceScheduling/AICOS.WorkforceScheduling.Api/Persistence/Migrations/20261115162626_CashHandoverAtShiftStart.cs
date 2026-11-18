using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.WorkforceScheduling.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CashHandoverAtShiftStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "opening_amount",
                schema: "workforce",
                table: "cash_closings",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "previous_closing_id",
                schema: "workforce",
                table: "cash_closings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "since_utc",
                schema: "workforce",
                table: "cash_closings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_cash_transactions_tenant_id_branch_id_occurred_at_utc",
                schema: "workforce",
                table: "cash_transactions",
                columns: new[] { "tenant_id", "branch_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_closings_tenant_id_branch_id_submitted_at_utc",
                schema: "workforce",
                table: "cash_closings",
                columns: new[] { "tenant_id", "branch_id", "submitted_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_cash_transactions_tenant_id_branch_id_occurred_at_utc",
                schema: "workforce",
                table: "cash_transactions");

            migrationBuilder.DropIndex(
                name: "ix_cash_closings_tenant_id_branch_id_submitted_at_utc",
                schema: "workforce",
                table: "cash_closings");

            migrationBuilder.DropColumn(
                name: "opening_amount",
                schema: "workforce",
                table: "cash_closings");

            migrationBuilder.DropColumn(
                name: "previous_closing_id",
                schema: "workforce",
                table: "cash_closings");

            migrationBuilder.DropColumn(
                name: "since_utc",
                schema: "workforce",
                table: "cash_closings");
        }
    }
}
