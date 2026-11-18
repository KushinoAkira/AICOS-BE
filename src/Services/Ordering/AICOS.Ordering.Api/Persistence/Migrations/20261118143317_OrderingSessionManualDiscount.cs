using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderingSessionManualDiscount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_discount_events",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    allocations_json = table.Column<string>(type: "jsonb", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_discount_events", x => x.id);
                    table.CheckConstraint("ck_session_discount_input", "mode IN ('AMOUNT','PERCENT') AND value >= 0 AND revision > 0");
                    table.ForeignKey(
                        name: "fk_session_discount_events_table_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "ordering",
                        principalTable: "table_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_session_discount_events_session_id",
                schema: "ordering",
                table: "session_discount_events",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_discount_events_tenant_id_session_id_revision",
                schema: "ordering",
                table: "session_discount_events",
                columns: new[] { "tenant_id", "session_id", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE ordering.session_discount_events IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM ordering.session_discount_events) THEN
                    RAISE EXCEPTION 'Discount audit contains data; disable feature and reconcile. Destructive rollback refused.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "session_discount_events",
                schema: "ordering");
        }
    }
}
