using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Communications;

namespace WhalePod.Infrastructure.Persistence;

public sealed partial class WhalePodDbContext
{
    private static void ConfigureCommunications(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmailOutbox>(b =>
        {
            b.ToTable("email_outbox", t =>
            {
                t.HasCheckConstraint("ck_email_outbox_status", "status IN ('pending','sending','sent','failed','suppressed')");
                t.HasCheckConstraint("ck_email_outbox_attempts", "attempts >= 0");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ToAddress).HasColumnType("citext").IsRequired();
            b.Property(x => x.Template).HasMaxLength(80).IsRequired();
            b.Property(x => x.PayloadJson).HasColumnType("jsonb");
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(EmailStatuses.Pending);
            b.Property(x => x.DedupeKey).HasMaxLength(200);
            b.Property(x => x.LastErrorCode).HasMaxLength(80);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.NextAttemptAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.ToAddress, x.DedupeKey }).IsUnique().HasFilter("dedupe_key IS NOT NULL").HasDatabaseName("uq_email_outbox_dedupe");
            b.HasIndex(x => x.NextAttemptAt).HasDatabaseName("ix_email_outbox_due").HasFilter("status IN ('pending','sending')");
            b.HasIndex(x => x.LockedUntil).HasDatabaseName("ix_email_outbox_locked").HasFilter("status = 'sending'");
            b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_email_outbox_status");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<EmailPreference>(b =>
        {
            b.ToTable("email_preference", t =>
            {
                t.HasCheckConstraint("ck_email_preference_type", "type IN ('replies_mentions','moderation_results','account_security')");
                t.HasCheckConstraint("ck_email_preference_security", "type <> 'account_security' OR enabled");
            });

            b.HasKey(x => new { x.UserId, x.Type });
            b.Property(x => x.Type).HasMaxLength(40).IsRequired();
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmailSuppression>(b =>
        {
            b.ToTable("email_suppression", t =>
            {
                t.HasCheckConstraint("ck_email_suppression_hash_length", "octet_length(email_hash) = 32");
                t.HasCheckConstraint("ck_email_suppression_reason", "reason IN ('hard_bounce','complaint','manual')");
            });

            b.HasKey(x => x.EmailHash);
            b.Property(x => x.Reason).HasMaxLength(30).IsRequired();
            b.Property(x => x.Source).HasMaxLength(80);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.LastSeenAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.Reason, x.CreatedAt }).HasDatabaseName("ix_email_suppression_reason");
        });
    }
}