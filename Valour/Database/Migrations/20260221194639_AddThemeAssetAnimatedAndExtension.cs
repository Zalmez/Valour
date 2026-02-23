using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Valour.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddThemeAssetAnimatedAndExtension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "animated",
                table: "theme_assets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "cdn_ext",
                table: "theme_assets",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "animated",
                table: "theme_assets");

            migrationBuilder.DropColumn(
                name: "cdn_ext",
                table: "theme_assets");
        }
    }
}
