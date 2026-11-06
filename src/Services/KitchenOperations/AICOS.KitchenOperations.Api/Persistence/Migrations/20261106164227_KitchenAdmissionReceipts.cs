using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.KitchenOperations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KitchenAdmissionReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "recovery_hold_reason",
                schema: "kitchen",
                table: "kitchen_tickets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "order_admission_receipts",
                schema: "kitchen",
                columns: table => new
                {
                    producer_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    consumer_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    creation_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    applied_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_verified_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_admission_receipts", x => new { x.producer_key, x.consumer_key, x.event_id });
                    table.CheckConstraint("ck_kitchen_admission_receipt_applied", "(receipt_status = 'APPLIED' AND applied_at_utc IS NOT NULL) OR (receipt_status = 'NEEDS_REVIEW' AND applied_at_utc IS NULL)");
                    table.CheckConstraint("ck_kitchen_admission_receipt_hash", "octet_length(creation_sha256) = 32");
                    table.ForeignKey(
                        name: "fk_order_admission_receipts_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "kitchen",
                        principalTable: "kitchen_tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_admission_receipt_order",
                schema: "kitchen",
                table: "order_admission_receipts",
                columns: new[] { "tenant_id", "order_id", "consumer_key" });

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_admission_receipt_scope",
                schema: "kitchen",
                table: "order_admission_receipts",
                columns: new[] { "tenant_id", "branch_id", "receipt_status" });

            migrationBuilder.CreateIndex(
                name: "ix_order_admission_receipts_ticket_id",
                schema: "kitchen",
                table: "order_admission_receipts",
                column: "ticket_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_admission_receipts",
                schema: "kitchen");

            migrationBuilder.DropColumn(
                name: "recovery_hold_reason",
                schema: "kitchen",
                table: "kitchen_tickets");
        }
    }
}
