using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.KitchenOperations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KitchenBaristaWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "barista_acceptances",
                schema: "kitchen",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accepted_by = table.Column<Guid>(type: "uuid", nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    accepted_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    accepted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_barista_acceptances", x => new { x.ticket_id, x.cycle });
                    table.CheckConstraint("ck_barista_cycle", "cycle >= 0 AND (completed_at_utc IS NULL OR completed_at_utc >= accepted_at_utc)");
                    table.ForeignKey(
                        name: "fk_barista_acceptances_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "kitchen",
                        principalTable: "kitchen_tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "kitchen_print_jobs",
                schema: "kitchen",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle = table.Column<int>(type: "integer", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    reprint_of = table.Column<Guid>(type: "uuid", nullable: true),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    table_snapshot = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    printed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    test_output = table.Column<bool>(type: "boolean", nullable: false),
                    error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    lease_owner = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    fence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kitchen_print_jobs", x => x.id);
                    table.CheckConstraint("ck_kitchen_print_ack", "(status = 'PRINTED' AND printed_at_utc IS NOT NULL) OR (status <> 'PRINTED' AND printed_at_utc IS NULL)");
                    table.CheckConstraint("ck_kitchen_print_kind", "kind IN ('SLIP','LABELS')");
                    table.CheckConstraint("ck_kitchen_print_state", "status IN ('QUEUED','PRINTING','PRINTED','FAILED','UNKNOWN') AND fence >= 0");
                    table.ForeignKey(
                        name: "fk_kitchen_print_jobs_kitchen_print_jobs_reprint_of",
                        column: x => x.reprint_of,
                        principalSchema: "kitchen",
                        principalTable: "kitchen_print_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_kitchen_print_jobs_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "kitchen",
                        principalTable: "kitchen_tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_barista_acceptances_tenant_id_accepted_by_key_hash",
                schema: "kitchen",
                table: "barista_acceptances",
                columns: new[] { "tenant_id", "accepted_by", "key_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_barista_acceptances_tenant_id_branch_id_accepted_at_utc",
                schema: "kitchen",
                table: "barista_acceptances",
                columns: new[] { "tenant_id", "branch_id", "accepted_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_print_jobs_reprint_of",
                schema: "kitchen",
                table: "kitchen_print_jobs",
                column: "reprint_of");

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_print_jobs_status_created_at_utc",
                schema: "kitchen",
                table: "kitchen_print_jobs",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_print_jobs_tenant_id_requested_by_key_hash",
                schema: "kitchen",
                table: "kitchen_print_jobs",
                columns: new[] { "tenant_id", "requested_by", "key_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_print_jobs_ticket_id_cycle",
                schema: "kitchen",
                table: "kitchen_print_jobs",
                columns: new[] { "ticket_id", "cycle" },
                unique: true,
                filter: "kind = 'SLIP' AND reprint_of IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "barista_acceptances",
                schema: "kitchen");

            migrationBuilder.DropTable(
                name: "kitchen_print_jobs",
                schema: "kitchen");
        }
    }
}
