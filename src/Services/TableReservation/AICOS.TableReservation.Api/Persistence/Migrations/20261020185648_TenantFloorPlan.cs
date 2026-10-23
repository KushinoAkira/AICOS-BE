using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.TableReservation.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantFloorPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reservations",
                schema: "table_reservation");

            // Tables used a free-text area and belonged to no tenant. Tables must now sit in an area of the floor plan, which cannot be
            // guessed from old demo data of the previous topic, so they are cleared: draw the floor plan again and print new QR codes.
            migrationBuilder.Sql("DELETE FROM table_reservation.dining_tables;");

            migrationBuilder.DropColumn(
                name: "area",
                schema: "table_reservation",
                table: "dining_tables");

            migrationBuilder.AddColumn<Guid>(
                name: "area_id",
                schema: "table_reservation",
                table: "dining_tables",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "qr_issued_at_utc",
                schema: "table_reservation",
                table: "dining_tables",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "table_reservation",
                table: "dining_tables",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "table_areas",
                schema: "table_reservation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_table_areas", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dining_tables_area_id",
                schema: "table_reservation",
                table: "dining_tables",
                column: "area_id");

            migrationBuilder.CreateIndex(
                name: "ix_dining_tables_tenant_id",
                schema: "table_reservation",
                table: "dining_tables",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_table_areas_branch_id_name",
                schema: "table_reservation",
                table: "table_areas",
                columns: new[] { "branch_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_table_areas_tenant_id",
                schema: "table_reservation",
                table: "table_areas",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "fk_dining_tables_table_areas_area_id",
                schema: "table_reservation",
                table: "dining_tables",
                column: "area_id",
                principalSchema: "table_reservation",
                principalTable: "table_areas",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_dining_tables_table_areas_area_id",
                schema: "table_reservation",
                table: "dining_tables");

            migrationBuilder.DropTable(
                name: "table_areas",
                schema: "table_reservation");

            migrationBuilder.DropIndex(
                name: "ix_dining_tables_area_id",
                schema: "table_reservation",
                table: "dining_tables");

            migrationBuilder.DropIndex(
                name: "ix_dining_tables_tenant_id",
                schema: "table_reservation",
                table: "dining_tables");

            migrationBuilder.DropColumn(
                name: "area_id",
                schema: "table_reservation",
                table: "dining_tables");

            migrationBuilder.DropColumn(
                name: "qr_issued_at_utc",
                schema: "table_reservation",
                table: "dining_tables");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "table_reservation",
                table: "dining_tables");

            migrationBuilder.AddColumn<string>(
                name: "area",
                schema: "table_reservation",
                table: "dining_tables",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "table_reservation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    customer_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    ends_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    party_size = table.Column<int>(type: "integer", nullable: false),
                    reserved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    table_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservations", x => x.id);
                    table.ForeignKey(
                        name: "fk_reservations_dining_tables_table_id",
                        column: x => x.table_id,
                        principalSchema: "table_reservation",
                        principalTable: "dining_tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_branch_id_reserved_at_utc",
                schema: "table_reservation",
                table: "reservations",
                columns: new[] { "branch_id", "reserved_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_customer_user_id",
                schema: "table_reservation",
                table: "reservations",
                column: "customer_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_table_id_reserved_at_utc",
                schema: "table_reservation",
                table: "reservations",
                columns: new[] { "table_id", "reserved_at_utc" });
        }
    }
}
