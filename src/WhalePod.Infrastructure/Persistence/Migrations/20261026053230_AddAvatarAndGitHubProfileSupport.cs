using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhalePod.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarAndGitHubProfileSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_media_upload_status",
                table: "media_upload");

            migrationBuilder.AddColumn<string>(
                name: "github_avatar_url",
                table: "user",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_media_upload_status",
                table: "media_upload",
                sql: "status IN ('temp','attached','avatar','deleted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_media_upload_status",
                table: "media_upload");

            migrationBuilder.DropColumn(
                name: "github_avatar_url",
                table: "user");

            migrationBuilder.AddCheckConstraint(
                name: "ck_media_upload_status",
                table: "media_upload",
                sql: "status IN ('temp','attached','deleted')");
        }
    }
}