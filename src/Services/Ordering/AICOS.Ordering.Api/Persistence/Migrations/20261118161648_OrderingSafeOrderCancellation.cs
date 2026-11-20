using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderingSafeOrderCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_cancellation_operations",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    table_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    before_json = table.Column<string>(type: "jsonb", nullable: false),
                    after_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    legacy_published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_cancellation_operations", x => x.id);
                    table.CheckConstraint("ck_order_cancellation_state", "state IN ('QUEUED','RESERVED','APPLIED','ABORTING','COMPLETED','REJECTED','ABORTED') AND attempts >= 0 AND version >= 0 AND ((state IN ('COMPLETED','REJECTED','ABORTED')) = (completed_at_utc IS NOT NULL))");
                    table.ForeignKey(
                        name: "fk_order_cancellation_operations_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "ordering",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_cancellation_operations_table_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "ordering",
                        principalTable: "table_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_cancellation_operations_next_attempt_at_utc",
                schema: "ordering",
                table: "order_cancellation_operations",
                column: "next_attempt_at_utc",
                filter: "completed_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_order_cancellation_operations_order_id",
                schema: "ordering",
                table: "order_cancellation_operations",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_cancellation_operations_session_id",
                schema: "ordering",
                table: "order_cancellation_operations",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_cancellation_operations_tenant_id_order_id",
                schema: "ordering",
                table: "order_cancellation_operations",
                columns: new[] { "tenant_id", "order_id" },
                unique: true,
                filter: "completed_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_order_cancellation_operations_tenant_id_session_id",
                schema: "ordering",
                table: "order_cancellation_operations",
                columns: new[] { "tenant_id", "session_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("LOCK TABLE ordering.order_cancellation_operations IN ACCESS EXCLUSIVE MODE; DO $$ BEGIN IF EXISTS (SELECT 1 FROM ordering.order_cancellation_operations) THEN RAISE EXCEPTION 'Refusing rollback: cancellation audit must be preserved'; END IF; END $$;");
            migrationBuilder.DropTable(
                name: "order_cancellation_operations",
                schema: "ordering");
        }
    }
}
