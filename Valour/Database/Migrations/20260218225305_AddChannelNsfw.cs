using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Valour.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelNsfw : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "nsfw",
                table: "channels",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "nsfw",
                table: "channels");
        }
    }
}
