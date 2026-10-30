using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhalePod.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseFencingAndCleanupBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "lease_token",
                table: "moderation_task",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "cleanup_attempts",
                table: "media_upload",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "media_upload",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_token",
                table: "email_outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_upload_retry",
                table: "media_upload",
                columns: new[] { "next_attempt_at", "expires_at" },
                filter: "status IN ('temp','deleted')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_media_upload_attempts",
                table: "media_upload",
                sql: "cleanup_attempts >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_media_upload_retry",
                table: "media_upload");

            migrationBuilder.DropCheckConstraint(
                name: "ck_media_upload_attempts",
                table: "media_upload");

            migrationBuilder.DropColumn(
                name: "lease_token",
                table: "moderation_task");

            migrationBuilder.DropColumn(
                name: "cleanup_attempts",
                table: "media_upload");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "media_upload");

            migrationBuilder.DropColumn(
                name: "lease_token",
                table: "email_outbox");
        }
    }
}