using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableTableTransfer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "table_transfer_operations",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    synced_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_table_transfer_operations", x => x.id);
                    table.ForeignKey(
                        name: "fk_table_transfer_operations_table_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "ordering",
                        principalTable: "table_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_table_transfer_operations_created_at_utc",
                schema: "ordering",
                table: "table_transfer_operations",
                column: "created_at_utc",
                filter: "synced_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_table_transfer_operations_session_id",
                schema: "ordering",
                table: "table_transfer_operations",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_table_transfer_operations_tenant_id_session_id_version",
                schema: "ordering",
                table: "table_transfer_operations",
                columns: new[] { "tenant_id", "session_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM ordering.table_transfer_operations) THEN RAISE EXCEPTION 'Transfer history is nonempty; rollback refused'; END IF; END $$;");
            migrationBuilder.DropTable(
                name: "table_transfer_operations",
                schema: "ordering");
        }
    }
}
