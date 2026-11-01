using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.CustomerEngagement.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StaffNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "staff_notifications",
                schema: "customer",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    audience = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    title = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    body = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staff_notification_reads",
                schema: "customer",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    read_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_notification_reads", x => new { x.notification_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_staff_notification_reads_staff_notifications_notification_id",
                        column: x => x.notification_id,
                        principalSchema: "customer",
                        principalTable: "staff_notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_staff_notifications_tenant_id_audience_branch_id_created_at",
                schema: "customer",
                table: "staff_notifications",
                columns: new[] { "tenant_id", "audience", "branch_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_staff_notifications_tenant_id_audience_dedupe_key",
                schema: "customer",
                table: "staff_notifications",
                columns: new[] { "tenant_id", "audience", "dedupe_key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_notification_reads",
                schema: "customer");

            migrationBuilder.DropTable(
                name: "staff_notifications",
                schema: "customer");
        }
    }
}
