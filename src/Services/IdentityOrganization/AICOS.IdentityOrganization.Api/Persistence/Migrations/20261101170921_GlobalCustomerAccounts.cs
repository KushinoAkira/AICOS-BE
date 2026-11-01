using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.IdentityOrganization.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GlobalCustomerAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "active_tenant_id",
                schema: "identity",
                table: "user_sessions",
                type: "uuid",
                nullable: true);

            // Customer accounts become platform-wide: the brand is chosen per session from now on.
            migrationBuilder.Sql("UPDATE identity.users SET tenant_id = NULL WHERE role = 'Customer';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "active_tenant_id",
                schema: "identity",
                table: "user_sessions");
        }
    }
}
