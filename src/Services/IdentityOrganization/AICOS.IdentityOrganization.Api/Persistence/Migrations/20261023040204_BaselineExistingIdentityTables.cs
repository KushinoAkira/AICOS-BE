using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.IdentityOrganization.Api.Persistence.Migrations;

/// <summary>
/// Records the six Identity tables that already exist in the supplied public schema.
/// This baseline intentionally performs no DDL and must never create or drop those tables.
/// </summary>
public partial class BaselineExistingIdentityTables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
