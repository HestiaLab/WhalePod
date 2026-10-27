using System;
using System.Text.Json;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhalePod.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    target_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ip_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    user_agent_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    metadata_json = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                    table.CheckConstraint("ck_audit_log_actor_type", "actor_type IN ('user','service','system')");
                });

            migrationBuilder.CreateTable(
                name: "category",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_category", x => x.id);
                    table.CheckConstraint("ck_category_name_length", "char_length(name) BETWEEN 1 AND 40");
                    table.CheckConstraint("ck_category_slug", "slug ~ '^[a-z0-9][a-z0-9-]*$'");
                    table.CheckConstraint("ck_category_status", "status IN ('active','disabled')");
                });

            migrationBuilder.CreateTable(
                name: "email_suppression",
                columns: table => new
                {
                    email_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_suppression", x => x.email_hash);
                    table.CheckConstraint("ck_email_suppression_hash_length", "octet_length(email_hash) = 32");
                    table.CheckConstraint("ck_email_suppression_reason", "reason IN ('hard_bounce','complaint','manual')");
                });

            migrationBuilder.CreateTable(
                name: "moderation_action",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    content_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_moderation_action", x => x.id);
                    table.CheckConstraint("ck_moderation_action_action", "action IN ('approve','reject','hide','restore','delete','escalate')");
                    table.CheckConstraint("ck_moderation_action_actor", "actor_type IN ('user','system')");
                    table.CheckConstraint("ck_moderation_action_from_status", "from_status IN ('pending','approved','rejected','hidden','deleted')");
                    table.CheckConstraint("ck_moderation_action_reason", "action NOT IN ('reject','hide','delete','escalate') OR (reason IS NOT NULL AND length(reason) > 0)");
                    table.CheckConstraint("ck_moderation_action_target", "target_type IN ('post','comment')");
                    table.CheckConstraint("ck_moderation_action_to_status", "to_status IN ('pending','approved','rejected','hidden','deleted')");
                    table.CheckConstraint("ck_moderation_action_version", "content_version > 0");
                });

            migrationBuilder.CreateTable(
                name: "moderation_check",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_version = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    check_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    risk_labels = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    raw_response = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_moderation_check", x => x.id);
                    table.CheckConstraint("ck_moderation_check_latency", "latency_ms IS NULL OR latency_ms >= 0");
                    table.CheckConstraint("ck_moderation_check_result", "result IN ('pass','review','reject','error')");
                    table.CheckConstraint("ck_moderation_check_target", "target_type IN ('post','comment')");
                    table.CheckConstraint("ck_moderation_check_type", "check_type IN ('text','image')");
                    table.CheckConstraint("ck_moderation_check_version", "content_version > 0");
                });

            migrationBuilder.CreateTable(
                name: "tag",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "citext", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tag", x => x.id);
                    table.CheckConstraint("ck_tag_name_length", "char_length(name) BETWEEN 1 AND 30");
                    table.CheckConstraint("ck_tag_slug", "slug ~ '^[a-z0-9][a-z0-9-]*$'");
                    table.CheckConstraint("ck_tag_status", "status IN ('active','hidden')");
                });

            migrationBuilder.CreateTable(
                name: "user",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    bio = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    avatar_url = table.Column<string>(type: "text", nullable: true),
                    github_id = table.Column<long>(type: "bigint", nullable: true),
                    github_id_hmac = table.Column<byte[]>(type: "bytea", nullable: true),
                    email = table.Column<string>(type: "citext", nullable: true),
                    email_verified = table.Column<bool>(type: "boolean", nullable: false),
                    trust_level = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    status_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "member"),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user", x => x.id);
                    table.CheckConstraint("ck_user_deleted_at", "status <> 'deleted' OR deleted_at IS NOT NULL");
                    table.CheckConstraint("ck_user_github_hmac_length", "github_id_hmac IS NULL OR octet_length(github_id_hmac) = 32");
                    table.CheckConstraint("ck_user_handle", "handle ~ '^[a-z0-9][a-z0-9-]*[a-z0-9]$' AND char_length(handle) BETWEEN 3 AND 32");
                    table.CheckConstraint("ck_user_role", "role IN ('member','admin')");
                    table.CheckConstraint("ck_user_status", "status IN ('active','muted','banned','deleted')");
                    table.CheckConstraint("ck_user_status_expiry", "status <> 'muted' OR status_expires_at IS NOT NULL");
                    table.CheckConstraint("ck_user_status_reason", "status NOT IN ('muted','banned') OR (status_reason IS NOT NULL AND length(status_reason) > 0)");
                    table.CheckConstraint("ck_user_trust_level", "trust_level BETWEEN 0 AND 4");
                });

            migrationBuilder.CreateTable(
                name: "appeal",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    appellant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    related_action_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    review_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appeal", x => x.id);
                    table.CheckConstraint("ck_appeal_reason_length", "char_length(reason) BETWEEN 1 AND 2000");
                    table.CheckConstraint("ck_appeal_resolution", "status = 'pending' OR (reviewed_by IS NOT NULL AND review_note IS NOT NULL AND length(review_note) > 0 AND resolved_at IS NOT NULL)");
                    table.CheckConstraint("ck_appeal_status", "status IN ('pending','accepted','rejected')");
                    table.CheckConstraint("ck_appeal_target", "target_type IN ('post','comment','account_penalty')");
                    table.ForeignKey(
                        name: "fk_appeal_moderation_action_related_action_id",
                        column: x => x.related_action_id,
                        principalTable: "moderation_action",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_appeal_user_appellant_id",
                        column: x => x.appellant_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_appeal_user_reviewed_by",
                        column: x => x.reviewed_by,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_key = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kernel_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    platform = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    label = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoke_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device", x => x.id);
                    table.CheckConstraint("ck_device_fingerprint", "fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_device_kernel_length", "char_length(kernel_version) BETWEEN 1 AND 64");
                    table.CheckConstraint("ck_device_label_length", "label IS NULL OR char_length(label) <= 40");
                    table.CheckConstraint("ck_device_platform", "platform IN ('win32-x64','win32-arm64','darwin-x64','darwin-arm64','linux-x64','linux-arm64')");
                    table.CheckConstraint("ck_device_revoke_reason", "status <> 'revoked' OR revoked_at IS NOT NULL");
                    table.CheckConstraint("ck_device_status", "status IN ('active','revoked')");
                    table.ForeignKey(
                        name: "fk_device_user_revoked_by",
                        column: x => x.revoked_by,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_device_user_user_id",
                        column: x => x.user_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_challenge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    request_ip_hash = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_challenge", x => x.id);
                    table.CheckConstraint("ck_device_challenge_attempts", "attempt_count >= 0");
                    table.CheckConstraint("ck_device_challenge_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_device_challenge_hash_length", "octet_length(challenge_hash) = 32");
                    table.ForeignKey(
                        name: "fk_device_challenge_user_user_id",
                        column: x => x.user_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "email_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_address = table.Column<string>(type: "citext", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    template = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payload_json = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    attempts = table.Column<short>(type: "smallint", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_outbox", x => x.id);
                    table.CheckConstraint("ck_email_outbox_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_email_outbox_status", "status IN ('pending','sending','sent','failed','suppressed')");
                    table.ForeignKey(
                        name: "fk_email_outbox_user_user_id",
                        column: x => x.user_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "email_preference",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_preference", x => new { x.user_id, x.type });
                    table.CheckConstraint("ck_email_preference_security", "type <> 'account_security' OR enabled");
                    table.CheckConstraint("ck_email_preference_type", "type IN ('replies_mentions','moderation_results','account_security')");
                    table.ForeignKey(
                        name: "fk_email_preference_user_user_id",
                        column: x => x.user_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "external_login",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "github"),
                    provider_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    provider_login = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_login", x => x.id);
                    table.CheckConstraint("ck_external_login_provider", "provider = 'github'");
                    table.ForeignKey(
                        name: "fk_external_login_user_user_id",
                        column: x => x.user_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invite_code",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    code_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    used_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invite_code", x => x.id);
                    table.CheckConstraint("ck_invite_code_hash_length", "octet_length(code_hash) = 32");
                    table.CheckConstraint("ck_invite_code_status", "status IN ('active','disabled')");
                    table.CheckConstraint("ck_invite_code_uses", "max_uses BETWEEN 1 AND 1000 AND used_count >= 0 AND used_count <= max_uses");
                    table.ForeignKey(
                        name: "fk_invite_code_user_created_by",
                        column: x => x.created_by,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    payload_json = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    occurrence_count = table.Column<int>(type: "integer", nullable: false),
                    last_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification", x => x.id);
                    table.CheckConstraint("ck_notification_occurrence", "occurrence_count >= 1");
                    table.CheckConstraint("ck_notification_type", "type IN ('reply','comment_accepted','mention','post_approved','post_rejected','comment_approved','comment_rejected','device_verified','device_revoked','account_status_changed','report_result')");
                    table.ForeignKey(
                        name: "fk_notification_user_user_id",
                        column: x => x.user_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "post",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body_md = table.Column<string>(type: "text", nullable: false),
                    body_html = table.Column<string>(type: "text", nullable: false),
                    excerpt = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    env_kernel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    env_platform = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    env_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    moderation_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    is_solved = table.Column<bool>(type: "boolean", nullable: false),
                    accepted_comment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    view_count = table.Column<int>(type: "integer", nullable: false),
                    copy_count = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post", x => x.id);
                    table.CheckConstraint("ck_post_body_length", "char_length(body_md) BETWEEN 10 AND 60000");
                    table.CheckConstraint("ck_post_counts", "view_count >= 0 AND copy_count >= 0");
                    table.CheckConstraint("ck_post_env_platform", "env_platform IS NULL OR env_platform IN ('win32-x64','win32-arm64','darwin-x64','darwin-arm64','linux-x64','linux-arm64')");
                    table.CheckConstraint("ck_post_moderation_status", "moderation_status IN ('pending','approved','rejected','hidden','deleted')");
                    table.CheckConstraint("ck_post_solved", "is_solved = (accepted_comment_id IS NOT NULL)");
                    table.CheckConstraint("ck_post_title_length", "char_length(title) BETWEEN 8 AND 200");
                    table.CheckConstraint("ck_post_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_post_category_category_id",
                        column: x => x.category_id,
                        principalTable: "category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_post_user_author_id",
                        column: x => x.author_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "report",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    handled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    handled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report", x => x.id);
                    table.CheckConstraint("ck_report_detail_length", "detail IS NULL OR char_length(detail) <= 1000");
                    table.CheckConstraint("ck_report_reason", "reason IN ('spam','harassment','misclassified','plagiarism','other')");
                    table.CheckConstraint("ck_report_resolution", "status = 'pending' OR (handled_by IS NOT NULL AND handled_at IS NOT NULL AND resolution_note IS NOT NULL AND length(resolution_note) > 0)");
                    table.CheckConstraint("ck_report_status", "status IN ('pending','resolved','dismissed')");
                    table.CheckConstraint("ck_report_target_type", "target_type IN ('post','comment','user')");
                    table.ForeignKey(
                        name: "fk_report_user_handled_by",
                        column: x => x.handled_by,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_report_user_reporter_id",
                        column: x => x.reporter_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sensitive_word",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    term = table.Column<string>(type: "citext", maxLength: 100, nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "manual"),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sensitive_word", x => x.id);
                    table.CheckConstraint("ck_sensitive_word_action", "action IN ('manual','reject')");
                    table.CheckConstraint("ck_sensitive_word_status", "status IN ('active','disabled')");
                    table.CheckConstraint("ck_sensitive_word_term_length", "char_length(term) BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "fk_sensitive_word_user_created_by",
                        column: x => x.created_by,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_account",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    ip_allowlist = table.Column<string[]>(type: "inet[]", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_account", x => x.id);
                    table.CheckConstraint("ck_service_account_scopes", "cardinality(scopes) > 0 AND scopes <@ ARRAY['moderation:read','moderation:suggest','agent:heartbeat']::text[]");
                    table.CheckConstraint("ck_service_account_status", "status IN ('active','disabled')");
                    table.ForeignKey(
                        name: "fk_service_account_user_owner_id",
                        column: x => x.owner_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "site_setting",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_setting", x => x.key);
                    table.ForeignKey(
                        name: "fk_site_setting_user_updated_by",
                        column: x => x.updated_by,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "invite_redemption",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invite_code_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invited_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invite_redemption", x => x.id);
                    table.ForeignKey(
                        name: "fk_invite_redemption_invite_code_invite_code_id",
                        column: x => x.invite_code_id,
                        principalTable: "invite_code",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invite_redemption_user_invited_by",
                        column: x => x.invited_by,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invite_redemption_user_user_id",
                        column: x => x.user_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "artifact",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    copy_count = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artifact", x => x.id);
                    table.CheckConstraint("ck_artifact_content_length", "char_length(content) BETWEEN 1 AND 200000");
                    table.CheckConstraint("ck_artifact_copy_count", "copy_count >= 0");
                    table.CheckConstraint("ck_artifact_kind", "kind IN ('patch','yaml','json','bundle','markdown','shell')");
                    table.CheckConstraint("ck_artifact_name_length", "char_length(name) BETWEEN 1 AND 120");
                    table.CheckConstraint("ck_artifact_note_length", "note IS NULL OR char_length(note) <= 200");
                    table.ForeignKey(
                        name: "fk_artifact_post_post_id",
                        column: x => x.post_id,
                        principalTable: "post",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body_md = table.Column<string>(type: "text", nullable: false),
                    body_html = table.Column<string>(type: "text", nullable: false),
                    moderation_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    is_accepted = table.Column<bool>(type: "boolean", nullable: false),
                    like_count = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment", x => x.id);
                    table.CheckConstraint("ck_comment_body_length", "char_length(body_md) BETWEEN 2 AND 10000");
                    table.CheckConstraint("ck_comment_like_count", "like_count >= 0");
                    table.CheckConstraint("ck_comment_moderation_status", "moderation_status IN ('pending','approved','rejected','hidden','deleted')");
                    table.CheckConstraint("ck_comment_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_comment_comment_parent_id",
                        column: x => x.parent_id,
                        principalTable: "comment",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_comment_post_post_id",
                        column: x => x.post_id,
                        principalTable: "post",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_comment_user_author_id",
                        column: x => x.author_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "media_upload",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    mime_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "temp"),
                    post_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    attached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() + interval '24 hours'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_upload", x => x.id);
                    table.CheckConstraint("ck_media_upload_attached_post", "status <> 'attached' OR post_id IS NOT NULL");
                    table.CheckConstraint("ck_media_upload_dimensions", "width > 0 AND height > 0");
                    table.CheckConstraint("ck_media_upload_mime", "mime_type IN ('image/jpeg','image/png','image/webp','image/gif')");
                    table.CheckConstraint("ck_media_upload_size", "byte_size BETWEEN 1 AND 10485760");
                    table.CheckConstraint("ck_media_upload_status", "status IN ('temp','attached','deleted')");
                    table.ForeignKey(
                        name: "fk_media_upload_post_post_id",
                        column: x => x.post_id,
                        principalTable: "post",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_media_upload_user_owner_id",
                        column: x => x.owner_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "post_like",
                columns: table => new
                {
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_like", x => new { x.post_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_post_like_post_post_id",
                        column: x => x.post_id,
                        principalTable: "post",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_post_like_user_user_id",
                        column: x => x.user_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "post_tag",
                columns: table => new
                {
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_tag", x => new { x.post_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_post_tag_post_post_id",
                        column: x => x.post_id,
                        principalTable: "post",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_post_tag_tag_tag_id",
                        column: x => x.tag_id,
                        principalTable: "tag",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_instance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    host_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    runtime_location = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    agent_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    mcp_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    model_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    model_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    model_quantization = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    model_runtime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    queue_depth = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    status_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_instance", x => x.id);
                    table.CheckConstraint("ck_agent_instance_host_fingerprint", "host_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_agent_instance_queue_depth", "queue_depth >= 0");
                    table.CheckConstraint("ck_agent_instance_runtime", "runtime_location = 'local' AND model_runtime = 'local'");
                    table.CheckConstraint("ck_agent_instance_status", "status IN ('active','paused','revoked')");
                    table.ForeignKey(
                        name: "fk_agent_instance_service_account_service_account_id",
                        column: x => x.service_account_id,
                        principalTable: "service_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_token",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    prefix = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_token", x => x.id);
                    table.CheckConstraint("ck_service_token_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_service_token_hash_length", "octet_length(token_hash) = 32");
                    table.ForeignKey(
                        name: "fk_service_token_service_account_service_account_id",
                        column: x => x.service_account_id,
                        principalTable: "service_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_service_token_user_revoked_by",
                        column: x => x.revoked_by,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "comment_like",
                columns: table => new
                {
                    comment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_like", x => new { x.comment_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_comment_like_comment_comment_id",
                        column: x => x.comment_id,
                        principalTable: "comment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_comment_like_user_user_id",
                        column: x => x.user_id,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "post_image",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    upload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    alt_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    mime_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    sort_order = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_image", x => x.id);
                    table.CheckConstraint("ck_post_image_alt_length", "alt_text IS NULL OR char_length(alt_text) <= 200");
                    table.CheckConstraint("ck_post_image_dimensions", "width > 0 AND height > 0");
                    table.CheckConstraint("ck_post_image_mime", "mime_type IN ('image/jpeg','image/png','image/webp','image/gif')");
                    table.CheckConstraint("ck_post_image_size", "byte_size BETWEEN 1 AND 10485760");
                    table.ForeignKey(
                        name: "fk_post_image_media_upload_upload_id",
                        column: x => x.upload_id,
                        principalTable: "media_upload",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_post_image_post_post_id",
                        column: x => x.post_id,
                        principalTable: "post",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "moderation_task",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_version = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "queued"),
                    priority = table.Column<short>(type: "smallint", nullable: false),
                    risk_hints_json = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    claimed_by_agent_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    task_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    suggested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    human_decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_moderation_task", x => x.id);
                    table.CheckConstraint("ck_moderation_task_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_moderation_task_lease", "claimed_at IS NULL OR lease_expires_at IS NULL OR lease_expires_at > claimed_at");
                    table.CheckConstraint("ck_moderation_task_priority", "priority BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_moderation_task_status", "status IN ('queued','claimed','suggested','human_decided','escalated','expired')");
                    table.CheckConstraint("ck_moderation_task_target", "target_type IN ('post','comment')");
                    table.CheckConstraint("ck_moderation_task_version", "content_version > 0");
                    table.ForeignKey(
                        name: "fk_moderation_task_agent_instances_claimed_by_agent_instance_id",
                        column: x => x.claimed_by_agent_instance_id,
                        principalTable: "agent_instance",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "moderation_suggestion",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    suggestion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    policy_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    runtime_location = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    model_runtime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    model_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    model_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    model_quantization = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    policy_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    token_usage = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    human_decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    human_decision_by = table.Column<Guid>(type: "uuid", nullable: true),
                    human_decision_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    human_decision_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    moderation_action_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_moderation_suggestion", x => x.id);
                    table.CheckConstraint("ck_moderation_suggestion_confidence", "confidence BETWEEN 0 AND 1");
                    table.CheckConstraint("ck_moderation_suggestion_human", "human_decision IS NULL AND human_decision_by IS NULL AND human_decision_at IS NULL OR human_decision IS NOT NULL AND human_decision_by IS NOT NULL AND human_decision_at IS NOT NULL");
                    table.CheckConstraint("ck_moderation_suggestion_human_value", "human_decision IS NULL OR human_decision IN ('approve','reject','hide','delete','escalate')");
                    table.CheckConstraint("ck_moderation_suggestion_latency", "latency_ms IS NULL OR latency_ms >= 0");
                    table.CheckConstraint("ck_moderation_suggestion_runtime", "runtime_location = 'local' AND model_runtime = 'local'");
                    table.CheckConstraint("ck_moderation_suggestion_type", "suggestion IN ('approve','reject','escalate')");
                    table.ForeignKey(
                        name: "fk_moderation_suggestion_agent_instances_agent_instance_id",
                        column: x => x.agent_instance_id,
                        principalTable: "agent_instance",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_moderation_suggestion_moderation_action_moderation_action_id",
                        column: x => x.moderation_action_id,
                        principalTable: "moderation_action",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_moderation_suggestion_moderation_task_task_id",
                        column: x => x.task_id,
                        principalTable: "moderation_task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_moderation_suggestion_service_accounts_service_account_id",
                        column: x => x.service_account_id,
                        principalTable: "service_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_moderation_suggestion_user_human_decision_by",
                        column: x => x.human_decision_by,
                        principalTable: "user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_heartbeat",
                table: "agent_instance",
                column: "last_heartbeat_at");

            migrationBuilder.CreateIndex(
                name: "ix_agent_instance_status",
                table: "agent_instance",
                columns: new[] { "service_account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "uq_agent_instance_name",
                table: "agent_instance",
                columns: new[] { "service_account_id", "instance_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_appeal_queue",
                table: "appeal",
                columns: new[] { "status", "created_at" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_appeal_related_action_id",
                table: "appeal",
                column: "related_action_id");

            migrationBuilder.CreateIndex(
                name: "ix_appeal_reviewed_by",
                table: "appeal",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "ix_appeal_target",
                table: "appeal",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "uq_appeal_pending",
                table: "appeal",
                columns: new[] { "appellant_id", "target_type", "target_id" },
                unique: true,
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_artifact_post",
                table: "artifact",
                columns: new[] { "post_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_action",
                table: "audit_log",
                columns: new[] { "action", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_actor",
                table: "audit_log",
                columns: new[] { "actor_type", "actor_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_request",
                table: "audit_log",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_target",
                table: "audit_log",
                columns: new[] { "target_type", "target_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_category_status_order",
                table: "category",
                columns: new[] { "status", "display_order" });

            migrationBuilder.CreateIndex(
                name: "uq_category_slug",
                table: "category",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_comment_author",
                table: "comment",
                columns: new[] { "author_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_comment_moderation_queue",
                table: "comment",
                column: "created_at",
                filter: "moderation_status = 'pending' AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_comment_parent",
                table: "comment",
                columns: new[] { "post_id", "parent_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_comment_parent_id",
                table: "comment",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_comment_thread",
                table: "comment",
                columns: new[] { "post_id", "moderation_status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_comment_accepted",
                table: "comment",
                column: "post_id",
                unique: true,
                filter: "is_accepted");

            migrationBuilder.CreateIndex(
                name: "uq_comment_id_post",
                table: "comment",
                columns: new[] { "id", "post_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_comment_like_user",
                table: "comment_like",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_revoked_by",
                table: "device",
                column: "revoked_by");

            migrationBuilder.CreateIndex(
                name: "ix_device_user_status",
                table: "device",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "uq_device_fingerprint",
                table: "device",
                column: "fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_device_public_key",
                table: "device",
                column: "public_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_challenge_pending",
                table: "device_challenge",
                columns: new[] { "user_id", "expires_at" },
                filter: "used_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_device_challenge_hash",
                table: "device_challenge",
                column: "challenge_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_due",
                table: "email_outbox",
                column: "next_attempt_at",
                filter: "status IN ('pending','sending')");

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_locked",
                table: "email_outbox",
                column: "locked_until",
                filter: "status = 'sending'");

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_status",
                table: "email_outbox",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_user_id",
                table: "email_outbox",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "uq_email_outbox_dedupe",
                table: "email_outbox",
                columns: new[] { "to_address", "dedupe_key" },
                unique: true,
                filter: "dedupe_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_email_suppression_reason",
                table: "email_suppression",
                columns: new[] { "reason", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_external_login_provider",
                table: "external_login",
                columns: new[] { "provider", "provider_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_external_login_user_provider",
                table: "external_login",
                columns: new[] { "user_id", "provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invite_code_admin",
                table: "invite_code",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_invite_code_created_by",
                table: "invite_code",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "uq_invite_code_hash",
                table: "invite_code",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invite_redemption_code",
                table: "invite_redemption",
                column: "invite_code_id");

            migrationBuilder.CreateIndex(
                name: "ix_invite_redemption_inviter",
                table: "invite_redemption",
                column: "invited_by");

            migrationBuilder.CreateIndex(
                name: "uq_invite_redemption_user",
                table: "invite_redemption",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_upload_cleanup",
                table: "media_upload",
                column: "expires_at",
                filter: "status IN ('temp','deleted')");

            migrationBuilder.CreateIndex(
                name: "ix_media_upload_owner_status",
                table: "media_upload",
                columns: new[] { "owner_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_media_upload_post_id",
                table: "media_upload",
                column: "post_id");

            migrationBuilder.CreateIndex(
                name: "uq_media_upload_url",
                table: "media_upload",
                column: "url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_moderation_action_actor",
                table: "moderation_action",
                columns: new[] { "actor_type", "actor_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_action_created",
                table: "moderation_action",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_moderation_action_target",
                table: "moderation_action",
                columns: new[] { "target_type", "target_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_check_result",
                table: "moderation_check",
                columns: new[] { "result", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_check_target",
                table: "moderation_check",
                columns: new[] { "target_type", "target_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_suggestion_agent_instance_id",
                table: "moderation_suggestion",
                column: "agent_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_moderation_suggestion_human_decision_by",
                table: "moderation_suggestion",
                column: "human_decision_by");

            migrationBuilder.CreateIndex(
                name: "ix_suggestion_quality",
                table: "moderation_suggestion",
                columns: new[] { "suggestion", "human_decision", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_suggestion_task",
                table: "moderation_suggestion",
                columns: new[] { "task_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_suggestion_action",
                table: "moderation_suggestion",
                column: "moderation_action_id",
                unique: true,
                filter: "moderation_action_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_suggestion_idempotency",
                table: "moderation_suggestion",
                columns: new[] { "service_account_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_moderation_task_claimed_by_agent_instance_id",
                table: "moderation_task",
                column: "claimed_by_agent_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_admin_queue",
                table: "moderation_task",
                columns: new[] { "status", "priority", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_task_agent_queue",
                table: "moderation_task",
                columns: new[] { "priority", "created_at" },
                filter: "status = 'queued'");

            migrationBuilder.CreateIndex(
                name: "ix_task_lease",
                table: "moderation_task",
                column: "lease_expires_at",
                filter: "status = 'claimed'");

            migrationBuilder.CreateIndex(
                name: "uq_moderation_task_active",
                table: "moderation_task",
                columns: new[] { "target_type", "target_id", "content_version" },
                unique: true,
                filter: "status IN ('queued','claimed','suggested','escalated')");

            migrationBuilder.CreateIndex(
                name: "ix_notification_unread",
                table: "notification",
                columns: new[] { "user_id", "created_at" },
                filter: "read_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_notification_dedupe",
                table: "notification",
                columns: new[] { "user_id", "dedupe_key" },
                unique: true,
                filter: "dedupe_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_post_author_feed",
                table: "post",
                columns: new[] { "author_id", "created_at" },
                filter: "moderation_status = 'approved' AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_post_author_workspace",
                table: "post",
                columns: new[] { "author_id", "moderation_status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_post_category_feed",
                table: "post",
                columns: new[] { "category_id", "created_at" },
                filter: "moderation_status = 'approved' AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_post_feed_active",
                table: "post",
                column: "last_activity_at",
                filter: "moderation_status = 'approved' AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_post_moderation_queue",
                table: "post",
                column: "created_at",
                filter: "moderation_status = 'pending' AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_post_accepted_comment",
                table: "post",
                column: "accepted_comment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_post_image_post",
                table: "post_image",
                columns: new[] { "post_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "uq_post_image_upload",
                table: "post_image",
                column: "upload_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_post_like_user",
                table: "post_like",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_post_tag_tag",
                table: "post_tag",
                columns: new[] { "tag_id", "post_id" });

            migrationBuilder.CreateIndex(
                name: "ix_report_handled_by",
                table: "report",
                column: "handled_by");

            migrationBuilder.CreateIndex(
                name: "ix_report_queue",
                table: "report",
                columns: new[] { "status", "created_at" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_report_reporter",
                table: "report",
                columns: new[] { "reporter_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_report_target",
                table: "report",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sensitive_word_created_by",
                table: "sensitive_word",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_sensitive_word_status",
                table: "sensitive_word",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "uq_sensitive_word_term",
                table: "sensitive_word",
                column: "term",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_account_owner",
                table: "service_account",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "uq_service_account_name",
                table: "service_account",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_token_revoked_by",
                table: "service_token",
                column: "revoked_by");

            migrationBuilder.CreateIndex(
                name: "ix_token_account_active",
                table: "service_token",
                columns: new[] { "service_account_id", "expires_at" },
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_service_token_hash",
                table: "service_token",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_site_setting_updated_by",
                table: "site_setting",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "ix_tag_status",
                table: "tag",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "uq_tag_name",
                table: "tag",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_tag_slug",
                table: "tag",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_activated_at",
                table: "user",
                column: "activated_at");

            migrationBuilder.CreateIndex(
                name: "ix_user_status",
                table: "user",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_user_trust_level",
                table: "user",
                column: "trust_level");

            migrationBuilder.CreateIndex(
                name: "uq_user_github_id",
                table: "user",
                column: "github_id",
                unique: true,
                filter: "github_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_user_github_id_hmac",
                table: "user",
                column: "github_id_hmac",
                unique: true,
                filter: "github_id_hmac IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_user_handle",
                table: "user",
                column: "handle",
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE post
                    ADD CONSTRAINT fk_post_accepted_comment_same_post
                    FOREIGN KEY (accepted_comment_id, id) REFERENCES comment (id, post_id)
                    DEFERRABLE INITIALLY DEFERRED;

                ALTER TABLE comment
                    ADD CONSTRAINT fk_comment_parent_same_post
                    FOREIGN KEY (parent_id, post_id) REFERENCES comment (id, post_id)
                    DEFERRABLE INITIALLY DEFERRED;

                CREATE INDEX ix_post_feed_latest ON post (created_at DESC, id DESC)
                    WHERE moderation_status = 'approved' AND deleted_at IS NULL;
                CREATE INDEX ix_post_feed_unresolved ON post (created_at DESC, id DESC)
                    WHERE moderation_status = 'approved' AND deleted_at IS NULL AND is_solved = false;
                CREATE INDEX ix_notification_user_time ON notification (user_id, created_at DESC, id DESC);

                CREATE INDEX ix_post_title_trgm ON post USING gin (title gin_trgm_ops)
                    WHERE moderation_status = 'approved' AND deleted_at IS NULL;
                CREATE INDEX ix_post_excerpt_trgm ON post USING gin (excerpt gin_trgm_ops)
                    WHERE moderation_status = 'approved' AND deleted_at IS NULL;
                CREATE INDEX ix_post_body_trgm ON post USING gin (body_md gin_trgm_ops)
                    WHERE moderation_status = 'approved' AND deleted_at IS NULL;
                CREATE INDEX ix_user_display_name_trgm ON "user" USING gin (display_name gin_trgm_ops);
                CREATE INDEX ix_user_handle_trgm ON "user" USING gin (handle gin_trgm_ops);
                CREATE INDEX ix_tag_name_trgm ON tag USING gin ((name::text) gin_trgm_ops);

                CREATE OR REPLACE FUNCTION prevent_audit_log_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_log is append-only';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_audit_log_append_only
                    BEFORE UPDATE OR DELETE ON audit_log
                    FOR EACH ROW EXECUTE FUNCTION prevent_audit_log_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE post DROP CONSTRAINT IF EXISTS fk_post_accepted_comment_same_post;");
            migrationBuilder.Sql("ALTER TABLE comment DROP CONSTRAINT IF EXISTS fk_comment_parent_same_post;");

            migrationBuilder.DropTable(
                name: "appeal");

            migrationBuilder.DropTable(
                name: "artifact");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "comment_like");

            migrationBuilder.DropTable(
                name: "device");

            migrationBuilder.DropTable(
                name: "device_challenge");

            migrationBuilder.DropTable(
                name: "email_outbox");

            migrationBuilder.DropTable(
                name: "email_preference");

            migrationBuilder.DropTable(
                name: "email_suppression");

            migrationBuilder.DropTable(
                name: "external_login");

            migrationBuilder.DropTable(
                name: "invite_redemption");

            migrationBuilder.DropTable(
                name: "moderation_check");

            migrationBuilder.DropTable(
                name: "moderation_suggestion");

            migrationBuilder.DropTable(
                name: "notification");

            migrationBuilder.DropTable(
                name: "post_image");

            migrationBuilder.DropTable(
                name: "post_like");

            migrationBuilder.DropTable(
                name: "post_tag");

            migrationBuilder.DropTable(
                name: "report");

            migrationBuilder.DropTable(
                name: "sensitive_word");

            migrationBuilder.DropTable(
                name: "service_token");

            migrationBuilder.DropTable(
                name: "site_setting");

            migrationBuilder.DropTable(
                name: "comment");

            migrationBuilder.DropTable(
                name: "invite_code");

            migrationBuilder.DropTable(
                name: "moderation_action");

            migrationBuilder.DropTable(
                name: "moderation_task");

            migrationBuilder.DropTable(
                name: "media_upload");

            migrationBuilder.DropTable(
                name: "tag");

            migrationBuilder.DropTable(
                name: "agent_instance");

            migrationBuilder.DropTable(
                name: "post");

            migrationBuilder.DropTable(
                name: "service_account");

            migrationBuilder.DropTable(
                name: "category");

            migrationBuilder.DropTable(
                name: "user");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS prevent_audit_log_mutation();");
        }
    }
}