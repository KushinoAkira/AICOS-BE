using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.IdentityOrganization.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LargerLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "logo_url",
                schema: "identity",
                table: "tenants",
                type: "character varying(200000)",
                maxLength: 200000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "logo_url",
                schema: "identity",
                table: "tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200000)",
                oldMaxLength: 200000,
                oldNullable: true);
        }
    }
}
