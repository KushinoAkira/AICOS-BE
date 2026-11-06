using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderingCupFulfillmentFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fulfillment_mode",
                schema: "ordering",
                table: "orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WholeOrder");

            migrationBuilder.CreateTable(
                name: "fulfillment_receipts",
                schema: "ordering",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_version = table.Column<long>(type: "bigint", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    applied_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fulfillment_receipts", x => x.event_id);
                    table.CheckConstraint("ck_fulfillment_receipt_payload", "source_version > 0 AND length(payload_hash) = 64 AND result IN ('Applied','Duplicate','Superseded')");
                    table.ForeignKey(
                        name: "fk_fulfillment_receipts_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "ordering",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_fulfillment_projection",
                schema: "ordering",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_version = table.Column<long>(type: "bigint", nullable: false),
                    snapshot_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    served = table.Column<int>(type: "integer", nullable: false),
                    review = table.Column<int>(type: "integer", nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    fulfilled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_verified_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_fulfillment_projection", x => x.order_id);
                    table.CheckConstraint("ck_order_fulfillment_completed", "(served = total) = (fulfilled_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_order_fulfillment_hash", "length(manifest_hash) = 64 AND length(snapshot_hash) = 64 AND jsonb_typeof(snapshot_json) = 'object'");
                    table.CheckConstraint("ck_order_fulfillment_summary", "source_version > 0 AND total BETWEEN 1 AND 4950 AND served BETWEEN 0 AND total AND review BETWEEN 0 AND total - served");
                    table.ForeignKey(
                        name: "fk_order_fulfillment_projection_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "ordering",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_fulfillment_mode",
                schema: "ordering",
                table: "orders",
                sql: "fulfillment_mode IN ('WholeOrder','CupV1')");

            migrationBuilder.CreateIndex(
                name: "ix_fulfillment_receipts_order_id",
                schema: "ordering",
                table: "fulfillment_receipts",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_fulfillment_receipts_tenant_id_order_id_source_version",
                schema: "ordering",
                table: "fulfillment_receipts",
                columns: new[] { "tenant_id", "order_id", "source_version" });

            migrationBuilder.CreateIndex(
                name: "ix_order_fulfillment_projection_tenant_id_branch_id_last_verif",
                schema: "ordering",
                table: "order_fulfillment_projection",
                columns: new[] { "tenant_id", "branch_id", "last_verified_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fulfillment_receipts",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "order_fulfillment_projection",
                schema: "ordering");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_fulfillment_mode",
                schema: "ordering",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "fulfillment_mode",
                schema: "ordering",
                table: "orders");
        }
    }
}
