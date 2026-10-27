using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhalePod.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppealResultNotificationType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_type",
                table: "notification");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_type",
                table: "notification",
                sql: "type IN ('reply','comment_accepted','mention','post_approved','post_rejected','comment_approved','comment_rejected','device_verified','device_revoked','account_status_changed','report_result','appeal_result')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_type",
                table: "notification");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_type",
                table: "notification",
                sql: "type IN ('reply','comment_accepted','mention','post_approved','post_rejected','comment_approved','comment_rejected','device_verified','device_revoked','account_status_changed','report_result')");
        }
    }
}