using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Platform;

namespace WhalePod.Infrastructure.Persistence;

public sealed partial class WhalePodDbContext
{
    private static void ConfigurePlatform(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLog>(b =>
        {
            b.ToTable("audit_log", t =>
            {
                t.HasCheckConstraint("ck_audit_log_actor_type", "actor_type IN ('user','service','system')");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ActorType).HasMaxLength(20).IsRequired();
            b.Property(x => x.Action).HasMaxLength(100).IsRequired();
            b.Property(x => x.TargetType).HasMaxLength(40);
            b.Property(x => x.RequestId).HasMaxLength(64);
            b.Property(x => x.MetadataJson).HasColumnType("jsonb");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.ActorType, x.ActorId, x.CreatedAt }).HasDatabaseName("ix_audit_actor");
            b.HasIndex(x => new { x.TargetType, x.TargetId, x.CreatedAt }).HasDatabaseName("ix_audit_target");
            b.HasIndex(x => new { x.Action, x.CreatedAt }).HasDatabaseName("ix_audit_action");
            b.HasIndex(x => x.RequestId).HasDatabaseName("ix_audit_request");
        });

        modelBuilder.Entity<SiteSetting>(b =>
        {
            b.ToTable("site_setting");
            b.HasKey(x => x.Key);
            b.Property(x => x.Key).HasMaxLength(100);
            b.Property(x => x.Value).HasColumnType("jsonb");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ServiceAccount>(b =>
        {
            b.ToTable("service_account", t =>
            {
                t.HasCheckConstraint("ck_service_account_status", "status IN ('active','disabled')");
                t.HasCheckConstraint("ck_service_account_scopes", "cardinality(scopes) > 0 AND scopes <@ ARRAY['moderation:read','moderation:suggest','agent:heartbeat']::text[]");
                t.HasCheckConstraint("ck_service_account_daily_token_budget", "daily_token_budget IS NULL OR daily_token_budget BETWEEN 1 AND 1000000000");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(80).IsRequired();
            b.Property(x => x.Scopes).HasColumnType("text[]").IsRequired();
            b.Property(x => x.IpAllowlist).HasColumnType("inet[]");
            b.Property(x => x.DailyTokenBudget).HasColumnName("daily_token_budget");
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(ServiceAccountStatuses.Active);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uq_service_account_name");
            b.HasIndex(x => x.OwnerId).HasDatabaseName("ix_service_account_owner");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ServiceToken>(b =>
        {
            b.ToTable("service_token", t =>
            {
                t.HasCheckConstraint("ck_service_token_hash_length", "octet_length(token_hash) = 32");
                t.HasCheckConstraint("ck_service_token_expiry", "expires_at > created_at");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TokenHash).IsRequired();
            b.Property(x => x.Prefix).HasMaxLength(12).IsRequired();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("uq_service_token_hash");
            b.HasIndex(x => new { x.ServiceAccountId, x.ExpiresAt }).HasDatabaseName("ix_token_account_active").HasFilter("revoked_at IS NULL");
            b.HasOne<ServiceAccount>().WithMany().HasForeignKey(x => x.ServiceAccountId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.RevokedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentInstance>(b =>
        {
            b.ToTable("agent_instance", t =>
            {
                t.HasCheckConstraint("ck_agent_instance_runtime", "runtime_location = 'local' AND model_runtime = 'local'");
                t.HasCheckConstraint("ck_agent_instance_status", "status IN ('active','paused','revoked')");
                t.HasCheckConstraint("ck_agent_instance_host_fingerprint", "host_fingerprint ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_agent_instance_queue_depth", "queue_depth >= 0");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.InstanceName).HasMaxLength(80).IsRequired();
            b.Property(x => x.HostFingerprint).HasMaxLength(64).IsRequired();
            b.Property(x => x.RuntimeLocation).HasMaxLength(20).IsRequired();
            b.Property(x => x.AgentVersion).HasMaxLength(40).IsRequired();
            b.Property(x => x.McpVersion).HasMaxLength(40);
            b.Property(x => x.ModelName).HasMaxLength(80).IsRequired();
            b.Property(x => x.ModelVersion).HasMaxLength(40).IsRequired();
            b.Property(x => x.ModelQuantization).HasMaxLength(40).IsRequired();
            b.Property(x => x.ModelRuntime).HasMaxLength(20).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(AgentInstanceStatuses.Active);
            b.Property(x => x.StatusReason).HasMaxLength(500);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.ServiceAccountId, x.InstanceName }).IsUnique().HasDatabaseName("uq_agent_instance_name");
            b.HasIndex(x => x.LastHeartbeatAt).HasDatabaseName("ix_agent_heartbeat");
            b.HasIndex(x => new { x.ServiceAccountId, x.Status }).HasDatabaseName("ix_agent_instance_status");
            b.HasOne<ServiceAccount>().WithMany().HasForeignKey(x => x.ServiceAccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}