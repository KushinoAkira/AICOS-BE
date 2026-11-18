using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.CustomerEngagement.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmAndLastPaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_paid_at_utc",
                schema: "customer",
                table: "customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "crm_campaign_sends",
                schema: "customer",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sent_on = table.Column<DateOnly>(type: "date", nullable: false),
                    voucher_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_campaign_sends", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_birthday",
                schema: "customer",
                table: "customers",
                columns: new[] { "tenant_id", "birthday" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_last_paid_at_utc",
                schema: "customer",
                table: "customers",
                columns: new[] { "tenant_id", "last_paid_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_campaign_sends_customer_id_type_sent_on",
                schema: "customer",
                table: "crm_campaign_sends",
                columns: new[] { "customer_id", "type", "sent_on" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_crm_campaign_sends_tenant_id_type_sent_on",
                schema: "customer",
                table: "crm_campaign_sends",
                columns: new[] { "tenant_id", "type", "sent_on" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crm_campaign_sends",
                schema: "customer");

            migrationBuilder.DropIndex(
                name: "ix_customers_tenant_id_birthday",
                schema: "customer",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "ix_customers_tenant_id_last_paid_at_utc",
                schema: "customer",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "last_paid_at_utc",
                schema: "customer",
                table: "customers");
        }
    }
}
