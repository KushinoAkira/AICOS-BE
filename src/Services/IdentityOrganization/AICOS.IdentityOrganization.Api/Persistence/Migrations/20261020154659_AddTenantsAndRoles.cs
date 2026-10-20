using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.IdentityOrganization.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantsAndRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_branches_name",
                schema: "identity",
                table: "branches");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "identity",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "identity",
                table: "branches",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id",
                schema: "identity",
                table: "users",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_branches_tenant_id_name",
                schema: "identity",
                table: "branches",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            // Existing single-tenant data moves into one "legacy" tenant, and old roles are renamed to the new actors.
            migrationBuilder.Sql(@"
                INSERT INTO identity.tenants (id, name, code, status, created_at_utc)
                SELECT '00000000-0000-0000-0000-00000000a1c0', 'Legacy brand', 'legacy', 'Active', now() at time zone 'utc'
                WHERE EXISTS (SELECT 1 FROM identity.branches) OR EXISTS (SELECT 1 FROM identity.users);

                UPDATE identity.branches SET tenant_id = '00000000-0000-0000-0000-00000000a1c0';
                UPDATE identity.users SET tenant_id = '00000000-0000-0000-0000-00000000a1c0';
                UPDATE identity.users SET role = 'BrandAdmin' WHERE role = 'SystemAdmin';
                UPDATE identity.users SET role = 'BranchManager' WHERE role = 'Manager';
                UPDATE identity.users SET role = 'Barista' WHERE role = 'WarehouseStaff';");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_code",
                schema: "identity",
                table: "tenants",
                column: "code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_branches_tenants_tenant_id",
                schema: "identity",
                table: "branches",
                column: "tenant_id",
                principalSchema: "identity",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_users_tenants_tenant_id",
                schema: "identity",
                table: "users",
                column: "tenant_id",
                principalSchema: "identity",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_branches_tenants_tenant_id",
                schema: "identity",
                table: "branches");

            migrationBuilder.DropForeignKey(
                name: "fk_users_tenants_tenant_id",
                schema: "identity",
                table: "users");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "ix_users_tenant_id",
                schema: "identity",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_branches_tenant_id_name",
                schema: "identity",
                table: "branches");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "identity",
                table: "branches");

            migrationBuilder.CreateIndex(
                name: "ix_branches_name",
                schema: "identity",
                table: "branches",
                column: "name",
                unique: true);
        }
    }
}
