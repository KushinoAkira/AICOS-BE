using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.TableReservation.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTablesReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "table_reservation");

            migrationBuilder.CreateTable(
                name: "dining_tables",
                schema: "table_reservation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    area = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    position_x = table.Column<int>(type: "integer", nullable: true),
                    position_y = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    qr_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dining_tables", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "table_reservation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    customer_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    party_size = table.Column<int>(type: "integer", nullable: false),
                    reserved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    ends_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
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
                name: "ix_dining_tables_branch_id_table_code",
                schema: "table_reservation",
                table: "dining_tables",
                columns: new[] { "branch_id", "table_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dining_tables_qr_token",
                schema: "table_reservation",
                table: "dining_tables",
                column: "qr_token",
                unique: true);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reservations",
                schema: "table_reservation");

            migrationBuilder.DropTable(
                name: "dining_tables",
                schema: "table_reservation");
        }
    }
}
