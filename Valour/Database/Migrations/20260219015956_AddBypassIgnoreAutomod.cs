using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Valour.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddBypassIgnoreAutomod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "run_for_everyone",
                table: "automod_triggers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "run_for_everyone",
                table: "automod_triggers");
        }
    }
}
