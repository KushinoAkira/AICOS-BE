using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.Ordering.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FeedbackReply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "replied_at_utc",
                schema: "ordering",
                table: "order_feedbacks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "replied_by_user_id",
                schema: "ordering",
                table: "order_feedbacks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reply",
                schema: "ordering",
                table: "order_feedbacks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "replied_at_utc",
                schema: "ordering",
                table: "order_feedbacks");

            migrationBuilder.DropColumn(
                name: "replied_by_user_id",
                schema: "ordering",
                table: "order_feedbacks");

            migrationBuilder.DropColumn(
                name: "reply",
                schema: "ordering",
                table: "order_feedbacks");
        }
    }
}
