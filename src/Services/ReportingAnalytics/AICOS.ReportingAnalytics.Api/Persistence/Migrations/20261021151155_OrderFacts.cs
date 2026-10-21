using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.ReportingAnalytics.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reporting");

            migrationBuilder.CreateTable(
                name: "order_facts",
                schema: "reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    placed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payment_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    paid_amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    refunded_amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_facts", x => x.id);
                    table.CheckConstraint("ck_order_facts_amounts", "total_amount >= 0 AND paid_amount >= 0 AND refunded_amount >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_facts_tenant_id_branch_id_paid_at_utc",
                schema: "reporting",
                table: "order_facts",
                columns: new[] { "tenant_id", "branch_id", "paid_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_order_facts_tenant_id_branch_id_placed_at_utc",
                schema: "reporting",
                table: "order_facts",
                columns: new[] { "tenant_id", "branch_id", "placed_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_facts",
                schema: "reporting");
        }
    }
}
