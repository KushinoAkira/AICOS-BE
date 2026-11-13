using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Payments.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentSessionBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_bills",
                schema: "payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quote_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    paid_payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ordering_acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    needs_reconciliation = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_bills", x => x.id);
                    table.CheckConstraint("ck_session_bill_status", "status IN ('UNPAID','PAID','CLOSED')");
                    table.CheckConstraint("ck_session_bill_total", "total >= 0");
                });

            migrationBuilder.CreateTable(
                name: "session_bill_attempts",
                schema: "payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    transaction_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    checkout_url = table.Column<string>(type: "text", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    tendered = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_bill_attempts", x => x.id);
                    table.CheckConstraint("ck_bill_attempt_amount", "amount > 0");
                    table.CheckConstraint("ck_bill_attempt_status", "status IN ('PENDING','SUCCEEDED','FAILED')");
                    table.ForeignKey(
                        name: "fk_session_bill_attempts_session_bills_bill_id",
                        column: x => x.bill_id,
                        principalSchema: "payment",
                        principalTable: "session_bills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_bill_events",
                schema: "payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    delivered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    lease_owner = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    fence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_bill_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_session_bill_events_session_bills_bill_id",
                        column: x => x.bill_id,
                        principalSchema: "payment",
                        principalTable: "session_bills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_bill_orders",
                schema: "payment",
                columns: table => new
                {
                    bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_bill_orders", x => new { x.bill_id, x.order_id });
                    table.ForeignKey(
                        name: "fk_session_bill_orders_session_bills_bill_id",
                        column: x => x.bill_id,
                        principalSchema: "payment",
                        principalTable: "session_bills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_session_bill_attempts_tenant_id_key_hash",
                schema: "payment",
                table: "session_bill_attempts",
                columns: new[] { "tenant_id", "key_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_bill_attempts_transaction_ref",
                schema: "payment",
                table: "session_bill_attempts",
                column: "transaction_ref",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_bill_pending_attempt",
                schema: "payment",
                table: "session_bill_attempts",
                column: "bill_id",
                unique: true,
                filter: "status = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "ix_session_bill_events_bill_id",
                schema: "payment",
                table: "session_bill_events",
                column: "bill_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_bill_events_next_attempt_at_utc_id",
                schema: "payment",
                table: "session_bill_events",
                columns: new[] { "next_attempt_at_utc", "id" },
                filter: "kind = 'PAID' AND delivered_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_session_bills_tenant_id_session_id",
                schema: "payment",
                table: "session_bills",
                columns: new[] { "tenant_id", "session_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_bill_attempts",
                schema: "payment");

            migrationBuilder.DropTable(
                name: "session_bill_events",
                schema: "payment");

            migrationBuilder.DropTable(
                name: "session_bill_orders",
                schema: "payment");

            migrationBuilder.DropTable(
                name: "session_bills",
                schema: "payment");
        }
    }
}
