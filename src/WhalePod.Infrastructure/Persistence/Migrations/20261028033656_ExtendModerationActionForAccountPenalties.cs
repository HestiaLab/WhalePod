using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhalePod.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtendModerationActionForAccountPenalties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_moderation_action_action",
                table: "moderation_action");

            migrationBuilder.DropCheckConstraint(
                name: "ck_moderation_action_from_status",
                table: "moderation_action");

            migrationBuilder.DropCheckConstraint(
                name: "ck_moderation_action_target",
                table: "moderation_action");

            migrationBuilder.DropCheckConstraint(
                name: "ck_moderation_action_to_status",
                table: "moderation_action");

            migrationBuilder.AddCheckConstraint(
                name: "ck_moderation_action_action",
                table: "moderation_action",
                sql: "action IN ('approve','reject','hide','restore','delete','escalate','mute','ban')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_moderation_action_from_status",
                table: "moderation_action",
                sql: "from_status IN ('pending','approved','rejected','hidden','deleted','active','muted','banned')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_moderation_action_target",
                table: "moderation_action",
                sql: "target_type IN ('post','comment','user')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_moderation_action_to_status",
                table: "moderation_action",
                sql: "to_status IN ('pending','approved','rejected','hidden','deleted','active','muted','banned')");

            // Existing muted/banned accounts predate penalty audit rows; bind them so appeals remain possible.
            migrationBuilder.Sql(
                """
                INSERT INTO moderation_action (id, target_type, target_id, actor_type, action,
                                               from_status, to_status, reason, content_version, created_at)
                SELECT gen_random_uuid(), 'user', u.id, 'system',
                       CASE WHEN u.status = 'muted' THEN 'mute' ELSE 'ban' END,
                       'active', u.status,
                       COALESCE(NULLIF(u.status_reason, ''), '历史处罚记录'),
                       1, COALESCE(u.updated_at, now())
                FROM "user" u
                WHERE u.status IN ('muted', 'banned')
                  AND NOT EXISTS (
                      SELECT 1 FROM moderation_action a
                      WHERE a.target_type = 'user'
                        AND a.target_id = u.id
                        AND a.action IN ('mute', 'ban'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_moderation_action_action",
                table: "moderation_action");

            migrationBuilder.DropCheckConstraint(
                name: "ck_moderation_action_from_status",
                table: "moderation_action");

            migrationBuilder.DropCheckConstraint(
                name: "ck_moderation_action_target",
                table: "moderation_action");

            migrationBuilder.DropCheckConstraint(
                name: "ck_moderation_action_to_status",
                table: "moderation_action");

            migrationBuilder.AddCheckConstraint(
                name: "ck_moderation_action_action",
                table: "moderation_action",
                sql: "action IN ('approve','reject','hide','restore','delete','escalate')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_moderation_action_from_status",
                table: "moderation_action",
                sql: "from_status IN ('pending','approved','rejected','hidden','deleted')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_moderation_action_target",
                table: "moderation_action",
                sql: "target_type IN ('post','comment')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_moderation_action_to_status",
                table: "moderation_action",
                sql: "to_status IN ('pending','approved','rejected','hidden','deleted')");
        }
    }
}