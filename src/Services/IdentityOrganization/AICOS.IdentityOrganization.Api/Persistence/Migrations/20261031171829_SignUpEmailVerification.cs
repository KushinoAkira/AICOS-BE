using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.IdentityOrganization.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SignUpEmailVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "google_subject",
                schema: "identity",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_google_subject",
                schema: "identity",
                table: "users",
                column: "google_subject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_google_subject",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "google_subject",
                schema: "identity",
                table: "users");
        }
    }
}
