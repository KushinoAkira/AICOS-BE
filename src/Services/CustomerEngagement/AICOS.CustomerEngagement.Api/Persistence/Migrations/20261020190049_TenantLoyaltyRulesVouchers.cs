using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.CustomerEngagement.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantLoyaltyRulesVouchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_vouchers_branch_id_status",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.DropIndex(
                name: "ix_vouchers_code",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.AlterColumn<Guid>(
                name: "branch_id",
                schema: "customer",
                table: "vouchers",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "customer",
                table: "vouchers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000a1c0"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "customer",
                table: "customers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000a1c0"));

            migrationBuilder.CreateTable(
                name: "loyalty_rules",
                schema: "customer",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    amount_per_point = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    redeem_value_per_point = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    min_points_to_redeem = table.Column<int>(type: "integer", nullable: false),
                    silver_threshold = table.Column<int>(type: "integer", nullable: false),
                    gold_threshold = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loyalty_rules", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vouchers_tenant_id_branch_id_status",
                schema: "customer",
                table: "vouchers",
                columns: new[] { "tenant_id", "branch_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_vouchers_tenant_id_code",
                schema: "customer",
                table: "vouchers",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id",
                schema: "customer",
                table: "customers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_rules_tenant_id",
                schema: "customer",
                table: "loyalty_rules",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loyalty_rules",
                schema: "customer");

            migrationBuilder.DropIndex(
                name: "ix_vouchers_tenant_id_branch_id_status",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.DropIndex(
                name: "ix_vouchers_tenant_id_code",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.DropIndex(
                name: "ix_customers_tenant_id",
                schema: "customer",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "customer",
                table: "vouchers");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "customer",
                table: "customers");

            migrationBuilder.AlterColumn<Guid>(
                name: "branch_id",
                schema: "customer",
                table: "vouchers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_vouchers_branch_id_status",
                schema: "customer",
                table: "vouchers",
                columns: new[] { "branch_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_vouchers_code",
                schema: "customer",
                table: "vouchers",
                column: "code",
                unique: true);
        }
    }
}
