using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Payments.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CancellationPolicyAndMerchantSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cancellation_policies",
                schema: "payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cancel_refund_percent = table.Column<int>(type: "integer", nullable: false),
                    no_show_after_minutes = table.Column<int>(type: "integer", nullable: false),
                    no_show_refund_percent = table.Column<int>(type: "integer", nullable: false),
                    unpaid_order_timeout_minutes = table.Column<int>(type: "integer", nullable: false),
                    auto_approve_within_policy = table.Column<bool>(type: "boolean", nullable: false),
                    exception_approver = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cancellation_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "merchant_settings",
                schema: "payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    account_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    bank_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    account_number_encrypted = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    account_number_hint = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    merchant_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merchant_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cancellation_policies_tenant_id",
                schema: "payment",
                table: "cancellation_policies",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_merchant_settings_tenant_id_branch_id_provider_code",
                schema: "payment",
                table: "merchant_settings",
                columns: new[] { "tenant_id", "branch_id", "provider_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cancellation_policies",
                schema: "payment");

            migrationBuilder.DropTable(
                name: "merchant_settings",
                schema: "payment");
        }
    }
}
