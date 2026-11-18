using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.KitchenOperations.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TableTransferRoutingVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "table_transfer_version",
                schema: "kitchen",
                table: "kitchen_tickets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM kitchen.kitchen_tickets WHERE table_transfer_version <> 0) THEN RAISE EXCEPTION 'Routing history exists; rollback refused'; END IF; END $$;");
            migrationBuilder.DropColumn(
                name: "table_transfer_version",
                schema: "kitchen",
                table: "kitchen_tickets");
        }
    }
}
