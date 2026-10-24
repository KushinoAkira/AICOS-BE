using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.CustomerEngagement.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDrinkPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "favorite_toppings",
                schema: "customer",
                table: "customers",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::text[]");

            migrationBuilder.AddColumn<string>(
                name: "preference_notes",
                schema: "customer",
                table: "customers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "preferred_ice",
                schema: "customer",
                table: "customers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "preferred_sugar_percent",
                schema: "customer",
                table: "customers",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "favorite_toppings",
                schema: "customer",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "preference_notes",
                schema: "customer",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "preferred_ice",
                schema: "customer",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "preferred_sugar_percent",
                schema: "customer",
                table: "customers");
        }
    }
}
