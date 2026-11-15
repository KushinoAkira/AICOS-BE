using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Payments.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BillSplitPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bill_split_plans",
                schema: "payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quote_json = table.Column<string>(type: "jsonb", nullable: false),
                    groups_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bill_split_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bill_split_operations",
                schema: "payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: false),
                    after_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bill_split_operations", x => x.id);
                    table.ForeignKey(
                        name: "fk_bill_split_operations_bill_split_plans_plan_id",
                        column: x => x.plan_id,
                        principalSchema: "payment",
                        principalTable: "bill_split_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bill_split_operations_plan_id",
                schema: "payment",
                table: "bill_split_operations",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_bill_split_plans_tenant_id_session_id",
                schema: "payment",
                table: "bill_split_plans",
                columns: new[] { "tenant_id", "session_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM payment.bill_split_plans) OR EXISTS (SELECT 1 FROM payment.bill_split_operations) THEN RAISE EXCEPTION 'Split audit is nonempty; rollback refused'; END IF; END $$;");
            migrationBuilder.DropTable(
                name: "bill_split_operations",
                schema: "payment");

            migrationBuilder.DropTable(
                name: "bill_split_plans",
                schema: "payment");
        }
    }
}
