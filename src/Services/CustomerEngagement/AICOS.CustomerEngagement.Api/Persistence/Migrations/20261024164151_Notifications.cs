using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.CustomerEngagement.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "customer",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    body = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    read_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_user_id",
                schema: "customer",
                table: "notifications",
                columns: new[] { "tenant_id", "user_id" },
                filter: "read_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_user_id_created_at_utc",
                schema: "customer",
                table: "notifications",
                columns: new[] { "tenant_id", "user_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_user_id_order_id_dedupe_key",
                schema: "customer",
                table: "notifications",
                columns: new[] { "tenant_id", "user_id", "order_id", "dedupe_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications",
                schema: "customer");
        }
    }
}
