using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Valour.Database.Context;

#nullable disable

namespace Valour.Database.Migrations;

[DbContext(typeof(ValourDb))]
[Migration("20260221000000_AddChannelAssociatedChat")]
public partial class AddChannelAssociatedChat : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "associated_chat_channel_id",
            table: "channels",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_channels_associated_chat_channel_id",
            table: "channels",
            column: "associated_chat_channel_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_channels_associated_chat_channel_id",
            table: "channels");

        migrationBuilder.DropColumn(
            name: "associated_chat_channel_id",
            table: "channels");
    }
}
