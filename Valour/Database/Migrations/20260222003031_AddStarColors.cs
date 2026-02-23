using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Valour.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddStarColors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "star_color_1",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "star_color_2",
                table: "users",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "star_color_1",
                table: "users");

            migrationBuilder.DropColumn(
                name: "star_color_2",
                table: "users");
        }
    }
}
