using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Valour.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeSubscriptionEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "stripe_payment_failed",
                table: "user_subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "stripe_payment_failed",
                table: "user_subscriptions");
        }
    }
}
