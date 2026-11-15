using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Payments.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CashierMoneyShifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cashier_shifts",
                schema: "payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    open_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    opening_cash = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    opened_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    counted_cash = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: true),
                    expected_cash = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: true),
                    closing_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cashier_shifts", x => x.id);
                    table.CheckConstraint("ck_cashier_shift_money", "opening_cash >= 0 AND (counted_cash IS NULL OR counted_cash >= 0)");
                });

            migrationBuilder.CreateTable(
                name: "collection_outbox",
                schema: "payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    delivered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_outbox", x => x.id);
                    table.CheckConstraint("ck_collection_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_collection_outbox_cashier_shifts_shift_id",
                        column: x => x.shift_id,
                        principalSchema: "payment",
                        principalTable: "cashier_shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cashier_shifts_tenant_id_cashier_id",
                schema: "payment",
                table: "cashier_shifts",
                columns: new[] { "tenant_id", "cashier_id" },
                unique: true,
                filter: "closed_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cashier_shifts_tenant_id_cashier_id_open_key",
                schema: "payment",
                table: "cashier_shifts",
                columns: new[] { "tenant_id", "cashier_id", "open_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_collection_outbox_paid_at_utc",
                schema: "payment",
                table: "collection_outbox",
                column: "paid_at_utc",
                filter: "delivered_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_collection_outbox_shift_id",
                schema: "payment",
                table: "collection_outbox",
                column: "shift_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM payment.collection_outbox) OR EXISTS (SELECT 1 FROM payment.cashier_shifts) THEN RAISE EXCEPTION 'Nonempty financial rollback refused; retain ledger and roll code forward'; END IF; END $$;");
            migrationBuilder.DropTable(
                name: "collection_outbox",
                schema: "payment");

            migrationBuilder.DropTable(
                name: "cashier_shifts",
                schema: "payment");
        }
    }
}
