using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.WorkforceScheduling.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkShiftSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_work_shifts_branch_id_name",
                schema: "workforce",
                table: "work_shifts");

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at_utc",
                schema: "workforce",
                table: "work_shifts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_work_shifts_branch_id_name",
                schema: "workforce",
                table: "work_shifts",
                columns: new[] { "branch_id", "name" },
                unique: true,
                filter: "deleted_at_utc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_work_shifts_branch_id_name",
                schema: "workforce",
                table: "work_shifts");

            migrationBuilder.DropColumn(
                name: "deleted_at_utc",
                schema: "workforce",
                table: "work_shifts");

            migrationBuilder.CreateIndex(
                name: "ix_work_shifts_branch_id_name",
                schema: "workforce",
                table: "work_shifts",
                columns: new[] { "branch_id", "name" },
                unique: true);
        }
    }
}
