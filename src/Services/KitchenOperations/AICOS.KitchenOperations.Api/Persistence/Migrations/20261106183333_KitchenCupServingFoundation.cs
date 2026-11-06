using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.KitchenOperations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KitchenCupServingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fulfillment_mode",
                schema: "kitchen",
                table: "kitchen_tickets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WholeOrder");

            migrationBuilder.AddColumn<long>(
                name: "fulfillment_version",
                schema: "kitchen",
                table: "kitchen_tickets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "source_session_id",
                schema: "kitchen",
                table: "kitchen_tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_table_id",
                schema: "kitchen",
                table: "kitchen_tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "order_item_id",
                schema: "kitchen",
                table: "kitchen_ticket_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cup_task_id",
                schema: "kitchen",
                table: "cup_stickers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cup_tasks",
                schema: "kitchen",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_index = table.Column<int>(type: "integer", nullable: false),
                    order_cup_index = table.Column<int>(type: "integer", nullable: false),
                    preparation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    service = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    preparation_cycle = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    fence = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ready_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    prepared_by = table.Column<Guid>(type: "uuid", nullable: true),
                    picked_up_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    served_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    served_by = table.Column<Guid>(type: "uuid", nullable: true),
                    review_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cup_tasks", x => x.id);
                    table.CheckConstraint("ck_cup_task_numbers", "unit_index > 0 AND order_cup_index > 0 AND preparation_cycle > 0 AND version >= 0 AND fence >= 0");
                    table.CheckConstraint("ck_cup_task_ready", "service NOT IN ('Available','Reserved','Carrying','Served') OR (preparation = 'Ready' AND ready_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_cup_task_review", "(service = 'Review') = (review_reason IS NOT NULL)");
                    table.CheckConstraint("ck_cup_task_served", "(service = 'Served') = (served_at_utc IS NOT NULL AND served_by IS NOT NULL)");
                    table.CheckConstraint("ck_cup_task_states", "preparation IN ('Pending','Brewing','Ready') AND service IN ('Waiting','Available','Reserved','Carrying','Served','Review')");
                    table.ForeignKey(
                        name: "fk_cup_tasks_ticket_items_ticket_item_id",
                        column: x => x.ticket_item_id,
                        principalSchema: "kitchen",
                        principalTable: "kitchen_ticket_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cup_tasks_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "kitchen",
                        principalTable: "kitchen_tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fulfillment_outbox",
                schema: "kitchen",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_version = table.Column<long>(type: "bigint", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fulfillment_outbox", x => x.event_id);
                    table.CheckConstraint("ck_fulfillment_outbox_ack", "(state = 'ACKED') = (acknowledged_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_fulfillment_outbox_lease", "(state = 'IN_FLIGHT' AND lease_token IS NOT NULL AND lease_until_utc IS NOT NULL) OR (state <> 'IN_FLIGHT' AND lease_token IS NULL AND lease_until_utc IS NULL)");
                    table.CheckConstraint("ck_fulfillment_outbox_payload", "jsonb_typeof(payload) = 'object' AND length(payload_hash) = 64");
                    table.CheckConstraint("ck_fulfillment_outbox_state", "state IN ('PENDING','IN_FLIGHT','ACKED','NEEDS_REVIEW') AND source_version > 0 AND attempt_count >= 0");
                });

            migrationBuilder.CreateTable(
                name: "cup_claims",
                schema: "kitchen",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cup_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    preparation_cycle = table.Column<int>(type: "integer", nullable: false),
                    fence = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    lease_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    picked_up_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ended_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cup_claims", x => x.id);
                    table.CheckConstraint("ck_cup_claim_lease", "lease_expires_at_utc > created_at_utc AND preparation_cycle > 0 AND fence > 0 AND version >= 0");
                    table.CheckConstraint("ck_cup_claim_state", "state IN ('Reserved','Carrying','Overdue','Served','Released','Overridden')");
                    table.CheckConstraint("ck_cup_claim_terminal", "(state IN ('Served','Released','Overridden')) = (ended_at_utc IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_cup_claims_cup_tasks_cup_id",
                        column: x => x.cup_id,
                        principalSchema: "kitchen",
                        principalTable: "cup_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cup_operations",
                schema: "kitchen",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cup_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    command = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version_before = table.Column<long>(type: "bigint", nullable: false),
                    version_after = table.Column<long>(type: "bigint", nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: false),
                    after_json = table.Column<string>(type: "jsonb", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    committed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cup_operations", x => x.id);
                    table.CheckConstraint("ck_cup_operation_audit", "version_after > version_before AND version_before >= 0 AND length(operation_key) > 0 AND length(request_hash) = 64");
                    table.CheckConstraint("ck_cup_operation_json", "jsonb_typeof(before_json) = 'object' AND jsonb_typeof(after_json) = 'object' AND jsonb_typeof(result_json) = 'object'");
                    table.ForeignKey(
                        name: "fk_cup_operations_cup_tasks_cup_id",
                        column: x => x.cup_id,
                        principalSchema: "kitchen",
                        principalTable: "cup_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_fulfillment_mode",
                schema: "kitchen",
                table: "kitchen_tickets",
                sql: "fulfillment_mode IN ('WholeOrder','CupV1') AND fulfillment_version >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_ticket_items_ticket_id_order_item_id",
                schema: "kitchen",
                table: "kitchen_ticket_items",
                columns: new[] { "ticket_id", "order_item_id" },
                unique: true,
                filter: "order_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cup_stickers_cup_task_id",
                schema: "kitchen",
                table: "cup_stickers",
                column: "cup_task_id",
                unique: true,
                filter: "cup_task_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cup_claims_state_lease_expires_at_utc",
                schema: "kitchen",
                table: "cup_claims",
                columns: new[] { "state", "lease_expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_cup_claims_tenant_id_branch_id_owner_user_id_state",
                schema: "kitchen",
                table: "cup_claims",
                columns: new[] { "tenant_id", "branch_id", "owner_user_id", "state" });

            migrationBuilder.CreateIndex(
                name: "uq_cup_claim_live",
                schema: "kitchen",
                table: "cup_claims",
                column: "cup_id",
                unique: true,
                filter: "state IN ('Reserved','Carrying','Overdue')");

            migrationBuilder.CreateIndex(
                name: "ix_cup_operations_cup_id_committed_at_utc",
                schema: "kitchen",
                table: "cup_operations",
                columns: new[] { "cup_id", "committed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_cup_operations_tenant_id_actor_id_operation_key",
                schema: "kitchen",
                table: "cup_operations",
                columns: new[] { "tenant_id", "actor_id", "operation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cup_tasks_tenant_id_branch_id_service_created_at_utc",
                schema: "kitchen",
                table: "cup_tasks",
                columns: new[] { "tenant_id", "branch_id", "service", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_cup_tasks_tenant_id_order_id_order_item_id_unit_index",
                schema: "kitchen",
                table: "cup_tasks",
                columns: new[] { "tenant_id", "order_id", "order_item_id", "unit_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cup_tasks_ticket_id_order_cup_index",
                schema: "kitchen",
                table: "cup_tasks",
                columns: new[] { "ticket_id", "order_cup_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cup_tasks_ticket_item_id",
                schema: "kitchen",
                table: "cup_tasks",
                column: "ticket_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_fulfillment_outbox_next_attempt_at_utc_lease_until_utc",
                schema: "kitchen",
                table: "fulfillment_outbox",
                columns: new[] { "next_attempt_at_utc", "lease_until_utc" },
                filter: "state IN ('PENDING','IN_FLIGHT')");

            migrationBuilder.CreateIndex(
                name: "ix_fulfillment_outbox_tenant_id_order_id_source_version",
                schema: "kitchen",
                table: "fulfillment_outbox",
                columns: new[] { "tenant_id", "order_id", "source_version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_cup_stickers_cup_tasks_cup_task_id",
                schema: "kitchen",
                table: "cup_stickers",
                column: "cup_task_id",
                principalSchema: "kitchen",
                principalTable: "cup_tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_cup_stickers_cup_tasks_cup_task_id",
                schema: "kitchen",
                table: "cup_stickers");

            migrationBuilder.DropTable(
                name: "cup_claims",
                schema: "kitchen");

            migrationBuilder.DropTable(
                name: "cup_operations",
                schema: "kitchen");

            migrationBuilder.DropTable(
                name: "fulfillment_outbox",
                schema: "kitchen");

            migrationBuilder.DropTable(
                name: "cup_tasks",
                schema: "kitchen");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_fulfillment_mode",
                schema: "kitchen",
                table: "kitchen_tickets");

            migrationBuilder.DropIndex(
                name: "ix_kitchen_ticket_items_ticket_id_order_item_id",
                schema: "kitchen",
                table: "kitchen_ticket_items");

            migrationBuilder.DropIndex(
                name: "ix_cup_stickers_cup_task_id",
                schema: "kitchen",
                table: "cup_stickers");

            migrationBuilder.DropColumn(
                name: "fulfillment_mode",
                schema: "kitchen",
                table: "kitchen_tickets");

            migrationBuilder.DropColumn(
                name: "fulfillment_version",
                schema: "kitchen",
                table: "kitchen_tickets");

            migrationBuilder.DropColumn(
                name: "source_session_id",
                schema: "kitchen",
                table: "kitchen_tickets");

            migrationBuilder.DropColumn(
                name: "source_table_id",
                schema: "kitchen",
                table: "kitchen_tickets");

            migrationBuilder.DropColumn(
                name: "order_item_id",
                schema: "kitchen",
                table: "kitchen_ticket_items");

            migrationBuilder.DropColumn(
                name: "cup_task_id",
                schema: "kitchen",
                table: "cup_stickers");
        }
    }
}
