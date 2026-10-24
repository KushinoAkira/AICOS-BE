using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TableSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "table_session_id",
                schema: "ordering",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "table_sessions",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opened_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_activity_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_table_sessions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_table_session_id",
                schema: "ordering",
                table: "orders",
                column: "table_session_id",
                filter: "table_session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_table_sessions_last_activity_at_utc",
                schema: "ordering",
                table: "table_sessions",
                column: "last_activity_at_utc",
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_table_sessions_tenant_id_branch_id_status",
                schema: "ordering",
                table: "table_sessions",
                columns: new[] { "tenant_id", "branch_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_table_sessions_tenant_id_table_id",
                schema: "ordering",
                table: "table_sessions",
                columns: new[] { "tenant_id", "table_id" },
                unique: true,
                filter: "status = 'Open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "table_sessions",
                schema: "ordering");

            migrationBuilder.DropIndex(
                name: "ix_orders_table_session_id",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "table_session_id",
                schema: "ordering",
                table: "orders");
        }
    }
}
