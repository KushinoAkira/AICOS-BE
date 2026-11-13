using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.MenuCatalog.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductPictureUpload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "image_url",
                schema: "menu",
                table: "products",
                type: "character varying(400000)",
                maxLength: 400000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "image_url",
                schema: "menu",
                table: "products",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(400000)",
                oldMaxLength: 400000,
                oldNullable: true);
        }
    }
}
