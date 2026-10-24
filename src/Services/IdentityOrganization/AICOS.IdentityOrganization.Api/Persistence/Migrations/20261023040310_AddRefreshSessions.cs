using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.IdentityOrganization.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class AddRefreshSessions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "refresh_sessions",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                replaced_by_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                version = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_refresh_sessions", x => x.id);
                table.ForeignKey(
                    name: "fk_refresh_sessions_branches_branch_id",
                    column: x => x.branch_id,
                    principalSchema: "public",
                    principalTable: "branches",
                    principalColumn: "id");
                table.ForeignKey(
                    name: "fk_refresh_sessions_refresh_sessions_replaced_by_session_id",
                    column: x => x.replaced_by_session_id,
                    principalSchema: "public",
                    principalTable: "refresh_sessions",
                    principalColumn: "id");
                table.ForeignKey(
                    name: "fk_refresh_sessions_users_user_id",
                    column: x => x.user_id,
                    principalSchema: "public",
                    principalTable: "users",
                    principalColumn: "id");
            });

        migrationBuilder.CreateIndex(
            name: "ix_refresh_sessions_branch_id",
            schema: "public",
            table: "refresh_sessions",
            column: "branch_id");

        migrationBuilder.CreateIndex(
            name: "ix_refresh_sessions_replaced_by_session_id",
            schema: "public",
            table: "refresh_sessions",
            column: "replaced_by_session_id");

        migrationBuilder.CreateIndex(
            name: "ix_refresh_sessions_token_hash",
            schema: "public",
            table: "refresh_sessions",
            column: "token_hash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_refresh_sessions_user_id_branch_id_expires_at_utc",
            schema: "public",
            table: "refresh_sessions",
            columns: new[] { "user_id", "branch_id", "expires_at_utc" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "refresh_sessions",
            schema: "public");
    }
}
