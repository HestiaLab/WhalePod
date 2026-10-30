using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;

namespace WhalePod.Infrastructure.Persistence;

public sealed partial class WhalePodDbContext
{
    private static void ConfigureModeration(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModerationAction>(b =>
        {
            b.ToTable("moderation_action", t =>
            {
                t.HasCheckConstraint("ck_moderation_action_target", "target_type IN ('post','comment','user')");
                t.HasCheckConstraint("ck_moderation_action_actor", "actor_type IN ('user','system')");
                t.HasCheckConstraint("ck_moderation_action_action", "action IN ('approve','reject','hide','restore','delete','escalate','mute','ban')");
                t.HasCheckConstraint("ck_moderation_action_from_status", "from_status IN ('pending','approved','rejected','hidden','deleted','active','muted','banned')");
                t.HasCheckConstraint("ck_moderation_action_to_status", "to_status IN ('pending','approved','rejected','hidden','deleted','active','muted','banned')");
                t.HasCheckConstraint("ck_moderation_action_reason", "action NOT IN ('reject','hide','delete','escalate') OR (reason IS NOT NULL AND length(reason) > 0)");
                t.HasCheckConstraint("ck_moderation_action_version", "content_version > 0");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TargetType).HasMaxLength(20).IsRequired();
            b.Property(x => x.ActorType).HasMaxLength(20).IsRequired();
            b.Property(x => x.Action).HasMaxLength(20).IsRequired();
            b.Property(x => x.FromStatus).HasMaxLength(20).IsRequired();
            b.Property(x => x.ToStatus).HasMaxLength(20).IsRequired();
            b.Property(x => x.Reason).HasMaxLength(500);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.TargetType, x.TargetId, x.CreatedAt }).HasDatabaseName("ix_moderation_action_target");
            b.HasIndex(x => new { x.ActorType, x.ActorId, x.CreatedAt }).HasDatabaseName("ix_moderation_action_actor");
            b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_moderation_action_created");
        });

        modelBuilder.Entity<ModerationTask>(b =>
        {
            b.ToTable("moderation_task", t =>
            {
                t.HasCheckConstraint("ck_moderation_task_target", "target_type IN ('post','comment')");
                t.HasCheckConstraint("ck_moderation_task_status", "status IN ('queued','claimed','suggested','human_decided','escalated','expired')");
                t.HasCheckConstraint("ck_moderation_task_version", "content_version > 0");
                t.HasCheckConstraint("ck_moderation_task_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_moderation_task_priority", "priority BETWEEN 0 AND 100");
                t.HasCheckConstraint("ck_moderation_task_lease", "claimed_at IS NULL OR lease_expires_at IS NULL OR lease_expires_at > claimed_at");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TargetType).HasMaxLength(20).IsRequired();
            b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(ModerationTaskStatuses.Queued);
            b.Property(x => x.RiskHintsJson).HasColumnType("jsonb");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.TargetType, x.TargetId, x.ContentVersion })
                .IsUnique()
                .HasFilter("status IN ('queued','claimed','suggested','escalated')")
                .HasDatabaseName("uq_moderation_task_active");
            b.HasIndex(x => new { x.Priority, x.CreatedAt }).HasDatabaseName("ix_task_agent_queue").HasFilter("status = 'queued'");
            b.HasIndex(x => x.LeaseExpiresAt).HasDatabaseName("ix_task_lease").HasFilter("status = 'claimed'");
            b.HasIndex(x => new { x.Status, x.Priority, x.CreatedAt }).HasDatabaseName("ix_task_admin_queue");
            b.HasOne<AgentInstance>().WithMany().HasForeignKey(x => x.ClaimedByAgentInstanceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ModerationSuggestion>(b =>
        {
            b.ToTable("moderation_suggestion", t =>
            {
                t.HasCheckConstraint("ck_moderation_suggestion_type", "suggestion IN ('approve','reject','escalate')");
                t.HasCheckConstraint("ck_moderation_suggestion_confidence", "confidence BETWEEN 0 AND 1");
                t.HasCheckConstraint("ck_moderation_suggestion_runtime", "runtime_location = 'local' AND model_runtime = 'local'");
                t.HasCheckConstraint("ck_moderation_suggestion_latency", "latency_ms IS NULL OR latency_ms >= 0");
                t.HasCheckConstraint("ck_moderation_suggestion_human", "human_decision IS NULL AND human_decision_by IS NULL AND human_decision_at IS NULL OR human_decision IS NOT NULL AND human_decision_by IS NOT NULL AND human_decision_at IS NOT NULL");
                t.HasCheckConstraint("ck_moderation_suggestion_human_value", "human_decision IS NULL OR human_decision IN ('approve','reject','hide','delete','escalate')");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Suggestion).HasMaxLength(20).IsRequired();
            b.Property(x => x.PolicyCode).HasMaxLength(80).IsRequired();
            b.Property(x => x.Reason).HasMaxLength(2000).IsRequired();
            b.Property(x => x.Confidence).HasPrecision(4, 3);
            b.Property(x => x.RuntimeLocation).HasMaxLength(20).IsRequired();
            b.Property(x => x.ModelRuntime).HasMaxLength(20).IsRequired();
            b.Property(x => x.ModelName).HasMaxLength(80).IsRequired();
            b.Property(x => x.ModelVersion).HasMaxLength(40).IsRequired();
            b.Property(x => x.ModelQuantization).HasMaxLength(40).IsRequired();
            b.Property(x => x.PromptVersion).HasMaxLength(40).IsRequired();
            b.Property(x => x.PolicyVersion).HasMaxLength(40).IsRequired();
            b.Property(x => x.TokenUsage).HasColumnType("jsonb");
            b.Property(x => x.HumanDecision).HasMaxLength(20);
            b.Property(x => x.HumanDecisionReason).HasMaxLength(1000);
            b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.ServiceAccountId, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_suggestion_idempotency");
            b.HasIndex(x => new { x.TaskId, x.CreatedAt }).HasDatabaseName("ix_suggestion_task");
            b.HasIndex(x => new { x.Suggestion, x.HumanDecision, x.CreatedAt }).HasDatabaseName("ix_suggestion_quality");
            b.HasIndex(x => x.ModerationActionId).IsUnique().HasFilter("moderation_action_id IS NOT NULL").HasDatabaseName("uq_suggestion_action");
            b.HasOne<ModerationTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ServiceAccount>().WithMany().HasForeignKey(x => x.ServiceAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<AgentInstance>().WithMany().HasForeignKey(x => x.AgentInstanceId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.HumanDecisionBy).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ModerationAction>().WithMany().HasForeignKey(x => x.ModerationActionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Appeal>(b =>
        {
            b.ToTable("appeal", t =>
            {
                t.HasCheckConstraint("ck_appeal_target", "target_type IN ('post','comment','account_penalty')");
                t.HasCheckConstraint("ck_appeal_status", "status IN ('pending','accepted','rejected')");
                t.HasCheckConstraint("ck_appeal_reason_length", "char_length(reason) BETWEEN 1 AND 2000");
                t.HasCheckConstraint("ck_appeal_resolution", "status = 'pending' OR (reviewed_by IS NOT NULL AND review_note IS NOT NULL AND length(review_note) > 0 AND resolved_at IS NOT NULL)");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TargetType).HasMaxLength(30).IsRequired();
            b.Property(x => x.Reason).HasMaxLength(2000).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(AppealStatuses.Pending);
            b.Property(x => x.ReviewNote).HasMaxLength(1000);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.AppellantId, x.TargetType, x.TargetId })
                .IsUnique()
                .HasFilter("status = 'pending'")
                .HasDatabaseName("uq_appeal_pending");
            b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_appeal_queue").HasFilter("status = 'pending'");
            b.HasIndex(x => new { x.TargetType, x.TargetId }).HasDatabaseName("ix_appeal_target");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.AppellantId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.ReviewedBy).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ModerationAction>().WithMany().HasForeignKey(x => x.RelatedActionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ModerationCheck>(b =>
        {
            b.ToTable("moderation_check", t =>
            {
                t.HasCheckConstraint("ck_moderation_check_target", "target_type IN ('post','comment')");
                t.HasCheckConstraint("ck_moderation_check_type", "check_type IN ('text','image')");
                t.HasCheckConstraint("ck_moderation_check_result", "result IN ('pass','review','reject','error')");
                t.HasCheckConstraint("ck_moderation_check_version", "content_version > 0");
                t.HasCheckConstraint("ck_moderation_check_latency", "latency_ms IS NULL OR latency_ms >= 0");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TargetType).HasMaxLength(20).IsRequired();
            b.Property(x => x.Provider).HasMaxLength(40).IsRequired();
            b.Property(x => x.CheckType).HasMaxLength(20).IsRequired();
            b.Property(x => x.Result).HasMaxLength(20).IsRequired();
            b.Property(x => x.RiskLabels).HasColumnType("jsonb");
            b.Property(x => x.RawResponse).HasColumnType("jsonb");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.TargetType, x.TargetId, x.CreatedAt }).HasDatabaseName("ix_moderation_check_target");
            b.HasIndex(x => new { x.Result, x.CreatedAt }).HasDatabaseName("ix_moderation_check_result");
        });

        modelBuilder.Entity<SensitiveWord>(b =>
        {
            b.ToTable("sensitive_word", t =>
            {
                t.HasCheckConstraint("ck_sensitive_word_term_length", "char_length(term) BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_sensitive_word_action", "action IN ('manual','reject')");
                t.HasCheckConstraint("ck_sensitive_word_status", "status IN ('active','disabled')");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Term).HasColumnType("citext").HasMaxLength(100).IsRequired();
            b.Property(x => x.Action).HasMaxLength(20).HasDefaultValue(SensitiveWordActions.Manual);
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(SensitiveWordStatuses.Active);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.Term).IsUnique().HasDatabaseName("uq_sensitive_word_term");
            b.HasIndex(x => x.Status).HasDatabaseName("ix_sensitive_word_status");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });
    }
}