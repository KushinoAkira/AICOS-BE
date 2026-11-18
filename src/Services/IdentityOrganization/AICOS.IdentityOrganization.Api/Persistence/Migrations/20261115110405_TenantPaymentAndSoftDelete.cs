using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.IdentityOrganization.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantPaymentAndSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "delete_reason",
                schema: "identity",
                table: "tenants",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at_utc",
                schema: "identity",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_claim_reference",
                schema: "identity",
                table: "tenants",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "requested_plan_id",
                schema: "identity",
                table: "tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at_utc",
                schema: "identity",
                table: "subscription_plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note",
                schema: "identity",
                table: "contact_messages",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            // A brand that never paid its plan waits for its first payment, like every new brand from now on.
            migrationBuilder.Sql("""
                UPDATE identity.tenants t SET status = 'AwaitingPayment'
                WHERE t.status = 'Active'
                  AND NOT EXISTS (SELECT 1 FROM identity.subscription_payments p WHERE p.tenant_id = t.id)
                  AND EXISTS (SELECT 1 FROM identity.tenant_subscriptions s WHERE s.tenant_id = t.id AND s.status = 'Active' AND s.fee > 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "delete_reason",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "deleted_at_utc",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "payment_claim_reference",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "requested_plan_id",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "deleted_at_utc",
                schema: "identity",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "note",
                schema: "identity",
                table: "contact_messages");
        }
    }
}
