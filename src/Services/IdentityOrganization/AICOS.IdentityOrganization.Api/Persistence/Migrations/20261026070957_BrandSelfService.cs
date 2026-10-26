using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.IdentityOrganization.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BrandSelfService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "description",
                schema: "identity",
                table: "tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logo_url",
                schema: "identity",
                table: "tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_color",
                schema: "identity",
                table: "tenants",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_note",
                schema: "identity",
                table: "tenants",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "reviewed_at_utc",
                schema: "identity",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reviewed_by",
                schema: "identity",
                table: "tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "secondary_color",
                schema: "identity",
                table: "tenants",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "contact_messages",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    subject = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    handled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    handled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenants_status",
                schema: "identity",
                table: "tenants",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_contact_messages_status_created_at_utc",
                schema: "identity",
                table: "contact_messages",
                columns: new[] { "status", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_messages",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "ix_tenants_status",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "description",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "logo_url",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "primary_color",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "review_note",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "reviewed_at_utc",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "reviewed_by",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "secondary_color",
                schema: "identity",
                table: "tenants");
        }
    }
}
