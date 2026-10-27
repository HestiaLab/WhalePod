using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Domain.Interactions;

namespace WhalePod.Infrastructure.Persistence;

public sealed partial class WhalePodDbContext
{
    private static void ConfigureInteractions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PostLike>(b =>
        {
            b.ToTable("post_like");
            b.HasKey(x => new { x.PostId, x.UserId });
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.UserId).HasDatabaseName("ix_post_like_user");
            b.HasOne<Post>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommentLike>(b =>
        {
            b.ToTable("comment_like");
            b.HasKey(x => new { x.CommentId, x.UserId });
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.UserId).HasDatabaseName("ix_comment_like_user");
            b.HasOne<Comment>().WithMany().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Report>(b =>
        {
            b.ToTable("report", t =>
            {
                t.HasCheckConstraint("ck_report_target_type", "target_type IN ('post','comment','user')");
                t.HasCheckConstraint("ck_report_reason", "reason IN ('spam','harassment','misclassified','plagiarism','other')");
                t.HasCheckConstraint("ck_report_status", "status IN ('pending','resolved','dismissed')");
                t.HasCheckConstraint("ck_report_detail_length", "detail IS NULL OR char_length(detail) <= 1000");
                t.HasCheckConstraint("ck_report_resolution", "status = 'pending' OR (handled_by IS NOT NULL AND handled_at IS NOT NULL AND resolution_note IS NOT NULL AND length(resolution_note) > 0)");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TargetType).HasMaxLength(20).IsRequired();
            b.Property(x => x.Reason).HasMaxLength(30).IsRequired();
            b.Property(x => x.Detail).HasMaxLength(1000);
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(ReportStatuses.Pending);
            b.Property(x => x.ResolutionNote).HasMaxLength(500);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_report_queue").HasFilter("status = 'pending'");
            b.HasIndex(x => new { x.TargetType, x.TargetId }).HasDatabaseName("ix_report_target");
            b.HasIndex(x => new { x.ReporterId, x.CreatedAt }).HasDatabaseName("ix_report_reporter");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.ReporterId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.HandledBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("notification", t =>
            {
                t.HasCheckConstraint("ck_notification_type", "type IN ('reply','comment_accepted','mention','post_approved','post_rejected','comment_approved','comment_rejected','device_verified','device_revoked','account_status_changed','report_result','appeal_result')");
                t.HasCheckConstraint("ck_notification_occurrence", "occurrence_count >= 1");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasMaxLength(40).IsRequired();
            b.Property(x => x.PayloadJson).HasColumnType("jsonb");
            b.Property(x => x.DedupeKey).HasMaxLength(200);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.LastOccurredAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.UserId, x.DedupeKey }).IsUnique().HasFilter("dedupe_key IS NOT NULL").HasDatabaseName("uq_notification_dedupe");
            b.HasIndex(x => new { x.UserId, x.CreatedAt }).HasDatabaseName("ix_notification_user_time");
            b.HasIndex(x => new { x.UserId, x.CreatedAt }).HasDatabaseName("ix_notification_unread").HasFilter("read_at IS NULL");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}