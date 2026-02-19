using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Valour.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddModerationAuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "moderation_audit_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    planet_id = table.Column<long>(type: "bigint", nullable: false),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    target_user_id = table.Column<long>(type: "bigint", nullable: true),
                    target_member_id = table.Column<long>(type: "bigint", nullable: true),
                    message_id = table.Column<long>(type: "bigint", nullable: true),
                    trigger_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<int>(type: "integer", nullable: false),
                    action_type = table.Column<int>(type: "integer", nullable: false),
                    details = table.Column<string>(type: "text", nullable: true),
                    time_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_audit_logs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_audit_logs_actor_user_id",
                table: "moderation_audit_logs",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_audit_logs_planet_id",
                table: "moderation_audit_logs",
                column: "planet_id");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_audit_logs_target_user_id",
                table: "moderation_audit_logs",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_audit_logs_time_created",
                table: "moderation_audit_logs",
                column: "time_created");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "moderation_audit_logs");
        }
    }
}
