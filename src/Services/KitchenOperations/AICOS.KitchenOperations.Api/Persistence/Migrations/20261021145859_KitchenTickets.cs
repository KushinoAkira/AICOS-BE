using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.KitchenOperations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KitchenTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "kitchen");

            migrationBuilder.CreateTable(
                name: "kitchen_tickets",
                schema: "kitchen",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    order_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_paid = table.Column<bool>(type: "boolean", nullable: false),
                    scheduled_pickup_time_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    brewing_started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ready_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kitchen_tickets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cup_stickers",
                schema: "kitchen",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    table_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    product_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    cup_index = table.Column<int>(type: "integer", nullable: false),
                    total_cups_in_order = table.Column<int>(type: "integer", nullable: false),
                    customizations = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    printed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cup_stickers", x => x.id);
                    table.ForeignKey(
                        name: "fk_cup_stickers_kitchen_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "kitchen",
                        principalTable: "kitchen_tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kitchen_ticket_items",
                schema: "kitchen",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kitchen_ticket_items", x => x.id);
                    table.CheckConstraint("ck_kitchen_ticket_items_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_kitchen_ticket_items_kitchen_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "kitchen",
                        principalTable: "kitchen_tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cup_stickers_ticket_id",
                schema: "kitchen",
                table: "cup_stickers",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_ticket_items_ticket_id",
                schema: "kitchen",
                table: "kitchen_ticket_items",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_tickets_tenant_id_branch_id_status_created_at_utc",
                schema: "kitchen",
                table: "kitchen_tickets",
                columns: new[] { "tenant_id", "branch_id", "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_tickets_tenant_id_order_id",
                schema: "kitchen",
                table: "kitchen_tickets",
                columns: new[] { "tenant_id", "order_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cup_stickers",
                schema: "kitchen");

            migrationBuilder.DropTable(
                name: "kitchen_ticket_items",
                schema: "kitchen");

            migrationBuilder.DropTable(
                name: "kitchen_tickets",
                schema: "kitchen");
        }
    }
}
