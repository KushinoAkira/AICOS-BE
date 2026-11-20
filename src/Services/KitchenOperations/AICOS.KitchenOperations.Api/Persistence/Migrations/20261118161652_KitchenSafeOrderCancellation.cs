using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.KitchenOperations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KitchenSafeOrderCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_cancellation_receipts",
                schema: "kitchen",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_cancellation_receipts", x => x.id);
                    table.CheckConstraint("ck_kitchen_cancellation_state", "state IN ('HELD','CANCELLED','ABORTED','REJECTED') AND ((state = 'HELD') = (completed_at_utc IS NULL))");
                    table.ForeignKey(
                        name: "fk_order_cancellation_receipts_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "kitchen",
                        principalTable: "kitchen_tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_cancellation_receipts_tenant_id_order_id",
                schema: "kitchen",
                table: "order_cancellation_receipts",
                columns: new[] { "tenant_id", "order_id" },
                unique: true,
                filter: "state = 'HELD'");

            migrationBuilder.CreateIndex(
                name: "ix_order_cancellation_receipts_ticket_id",
                schema: "kitchen",
                table: "order_cancellation_receipts",
                column: "ticket_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("LOCK TABLE kitchen.order_cancellation_receipts IN ACCESS EXCLUSIVE MODE; DO $$ BEGIN IF EXISTS (SELECT 1 FROM kitchen.order_cancellation_receipts) THEN RAISE EXCEPTION 'Refusing rollback: cancellation receipts must be preserved'; END IF; END $$;");
            migrationBuilder.DropTable(
                name: "order_cancellation_receipts",
                schema: "kitchen");
        }
    }
}
