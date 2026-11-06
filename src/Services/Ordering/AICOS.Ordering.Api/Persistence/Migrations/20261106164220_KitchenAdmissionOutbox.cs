using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KitchenAdmissionOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kitchen_admission_outbox",
                schema: "ordering",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    creation_payload = table.Column<string>(type: "jsonb", nullable: false),
                    creation_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    origin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kitchen_admission_outbox", x => x.event_id);
                    table.CheckConstraint("ck_kitchen_admission_ack", "(state = 'ACKED' AND acknowledged_at_utc IS NOT NULL AND acknowledged_ticket_id IS NOT NULL) OR (state <> 'ACKED' AND acknowledged_at_utc IS NULL AND acknowledged_ticket_id IS NULL)");
                    table.CheckConstraint("ck_kitchen_admission_lease", "(state = 'IN_FLIGHT' AND lease_token IS NOT NULL AND lease_until_utc IS NOT NULL) OR (state <> 'IN_FLIGHT' AND lease_token IS NULL AND lease_until_utc IS NULL)");
                    table.CheckConstraint("ck_kitchen_admission_payload", "jsonb_typeof(creation_payload) = 'object' AND octet_length(creation_sha256) = 32 AND schema_version > 0 AND attempt_count >= 0 AND origin IN ('CAPTURE','RECONCILE')");
                    table.CheckConstraint("ck_kitchen_admission_state", "state IN ('PENDING','IN_FLIGHT','ACKED','NEEDS_REVIEW')");
                });

            migrationBuilder.CreateTable(
                name: "kitchen_admission_reconcile_state",
                schema: "ordering",
                columns: table => new
                {
                    consumer_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    upper_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cycle_started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kitchen_admission_reconcile_state", x => x.consumer_key);
                    table.CheckConstraint("ck_kitchen_reconcile_cursor", "last_order_id IS NULL OR (upper_order_id IS NOT NULL AND last_order_id <= upper_order_id)");
                    table.CheckConstraint("ck_kitchen_reconcile_lease", "(lease_token IS NULL AND lease_until_utc IS NULL) OR (lease_token IS NOT NULL AND lease_until_utc IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_admission_due",
                schema: "ordering",
                table: "kitchen_admission_outbox",
                columns: new[] { "next_attempt_at_utc", "lease_until_utc", "event_id" },
                filter: "state IN ('PENDING','IN_FLIGHT')");

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_admission_scope_status",
                schema: "ordering",
                table: "kitchen_admission_outbox",
                columns: new[] { "tenant_id", "branch_id", "state", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "uq_kitchen_admission_order",
                schema: "ordering",
                table: "kitchen_admission_outbox",
                columns: new[] { "tenant_id", "order_id", "consumer_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kitchen_admission_outbox",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "kitchen_admission_reconcile_state",
                schema: "ordering");
        }
    }
}
