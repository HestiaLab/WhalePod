using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;

namespace WhalePod.Infrastructure.Persistence;

public sealed partial class WhalePodDbContext
{
    private static void ConfigureContent(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(b =>
        {
            b.ToTable("category", t =>
            {
                t.HasCheckConstraint("ck_category_status", "status IN ('active','disabled')");
                t.HasCheckConstraint("ck_category_slug", "slug ~ '^[a-z0-9][a-z0-9-]*$'");
                t.HasCheckConstraint("ck_category_name_length", "char_length(name) BETWEEN 1 AND 40");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Slug).HasMaxLength(40).IsRequired();
            b.Property(x => x.Name).HasMaxLength(40).IsRequired();
            b.Property(x => x.Description).HasMaxLength(200);
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(CategoryStatuses.Active);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("uq_category_slug");
            b.HasIndex(x => new { x.Status, x.DisplayOrder }).HasDatabaseName("ix_category_status_order");
        });

        modelBuilder.Entity<Post>(b =>
        {
            b.ToTable("post", t =>
            {
                t.HasCheckConstraint("ck_post_title_length", "char_length(title) BETWEEN 8 AND 200");
                t.HasCheckConstraint("ck_post_body_length", "char_length(body_md) BETWEEN 10 AND 60000");
                t.HasCheckConstraint("ck_post_moderation_status", "moderation_status IN ('pending','approved','rejected','hidden','deleted')");
                t.HasCheckConstraint("ck_post_env_platform", "env_platform IS NULL OR env_platform IN ('win32-x64','win32-arm64','darwin-x64','darwin-arm64','linux-x64','linux-arm64')");
                t.HasCheckConstraint("ck_post_solved", "is_solved = (accepted_comment_id IS NOT NULL)");
                t.HasCheckConstraint("ck_post_counts", "view_count >= 0 AND copy_count >= 0");
                t.HasCheckConstraint("ck_post_version", "version > 0");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.BodyMd).IsRequired();
            b.Property(x => x.BodyHtml).IsRequired();
            b.Property(x => x.Excerpt).HasMaxLength(320).IsRequired();
            b.Property(x => x.EnvKernel).HasMaxLength(64);
            b.Property(x => x.EnvPlatform).HasMaxLength(30);
            b.Property(x => x.EnvModel).HasMaxLength(100);
            b.Property(x => x.ModerationStatus).HasMaxLength(20).HasDefaultValue(ModerationStatuses.Pending);
            b.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.LastActivityAt).HasDefaultValueSql("now()");

            b.HasIndex(x => x.AcceptedCommentId).IsUnique().HasDatabaseName("uq_post_accepted_comment");
            b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_post_feed_latest").HasFilter("moderation_status = 'approved' AND deleted_at IS NULL");
            b.HasIndex(x => x.LastActivityAt).HasDatabaseName("ix_post_feed_active").HasFilter("moderation_status = 'approved' AND deleted_at IS NULL");
            b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_post_feed_unresolved").HasFilter("moderation_status = 'approved' AND deleted_at IS NULL AND is_solved = false");
            b.HasIndex(x => new { x.CategoryId, x.CreatedAt }).HasDatabaseName("ix_post_category_feed").HasFilter("moderation_status = 'approved' AND deleted_at IS NULL");
            b.HasIndex(x => new { x.AuthorId, x.CreatedAt }).HasDatabaseName("ix_post_author_feed").HasFilter("moderation_status = 'approved' AND deleted_at IS NULL");
            b.HasIndex(x => new { x.AuthorId, x.ModerationStatus, x.UpdatedAt }).HasDatabaseName("ix_post_author_workspace");
            b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_post_moderation_queue").HasFilter("moderation_status = 'pending' AND deleted_at IS NULL");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MediaUpload>(b =>
        {
            b.ToTable("media_upload", t =>
            {
                t.HasCheckConstraint("ck_media_upload_status", "status IN ('temp','attached','avatar','deleted')");
                t.HasCheckConstraint("ck_media_upload_dimensions", "width > 0 AND height > 0");
                t.HasCheckConstraint("ck_media_upload_size", "byte_size BETWEEN 1 AND 10485760");
                t.HasCheckConstraint("ck_media_upload_mime", "mime_type IN ('image/jpeg','image/png','image/webp','image/gif')");
                t.HasCheckConstraint("ck_media_upload_attached_post", "status <> 'attached' OR post_id IS NOT NULL");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Url).IsRequired();
            b.Property(x => x.MimeType).HasMaxLength(50).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(MediaUploadStatuses.Temp);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.ExpiresAt).HasDefaultValueSql("now() + interval '24 hours'");
            b.HasIndex(x => x.Url).IsUnique().HasDatabaseName("uq_media_upload_url");
            b.HasIndex(x => new { x.OwnerId, x.Status }).HasDatabaseName("ix_media_upload_owner_status");
            b.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_media_upload_cleanup").HasFilter("status IN ('temp','deleted')");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Post>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PostImage>(b =>
        {
            b.ToTable("post_image", t =>
            {
                t.HasCheckConstraint("ck_post_image_dimensions", "width > 0 AND height > 0");
                t.HasCheckConstraint("ck_post_image_size", "byte_size BETWEEN 1 AND 10485760");
                t.HasCheckConstraint("ck_post_image_mime", "mime_type IN ('image/jpeg','image/png','image/webp','image/gif')");
                t.HasCheckConstraint("ck_post_image_alt_length", "alt_text IS NULL OR char_length(alt_text) <= 200");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Url).IsRequired();
            b.Property(x => x.MimeType).HasMaxLength(50).IsRequired();
            b.Property(x => x.AltText).HasMaxLength(200);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.UploadId).IsUnique().HasDatabaseName("uq_post_image_upload");
            b.HasIndex(x => new { x.PostId, x.SortOrder }).HasDatabaseName("ix_post_image_post");
            b.HasOne<Post>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<MediaUpload>().WithMany().HasForeignKey(x => x.UploadId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Tag>(b =>
        {
            b.ToTable("tag", t =>
            {
                t.HasCheckConstraint("ck_tag_status", "status IN ('active','hidden')");
                t.HasCheckConstraint("ck_tag_slug", "slug ~ '^[a-z0-9][a-z0-9-]*$'");
                t.HasCheckConstraint("ck_tag_name_length", "char_length(name) BETWEEN 1 AND 30");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Slug).HasMaxLength(40).IsRequired();
            b.Property(x => x.Name).HasColumnType("citext").HasMaxLength(30).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(TagStatuses.Active);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("uq_tag_slug");
            b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uq_tag_name");
            b.HasIndex(x => x.Status).HasDatabaseName("ix_tag_status");
        });

        modelBuilder.Entity<PostTag>(b =>
        {
            b.ToTable("post_tag");
            b.HasKey(x => new { x.PostId, x.TagId });
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.TagId, x.PostId }).HasDatabaseName("ix_post_tag_tag");
            b.HasOne<Post>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Artifact>(b =>
        {
            b.ToTable("artifact", t =>
            {
                t.HasCheckConstraint("ck_artifact_kind", "kind IN ('patch','yaml','json','bundle','markdown','shell')");
                t.HasCheckConstraint("ck_artifact_name_length", "char_length(name) BETWEEN 1 AND 120");
                t.HasCheckConstraint("ck_artifact_note_length", "note IS NULL OR char_length(note) <= 200");
                t.HasCheckConstraint("ck_artifact_content_length", "char_length(content) BETWEEN 1 AND 200000");
                t.HasCheckConstraint("ck_artifact_copy_count", "copy_count >= 0");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            b.Property(x => x.Name).HasMaxLength(120).IsRequired();
            b.Property(x => x.Note).HasMaxLength(200);
            b.Property(x => x.Content).IsRequired();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.PostId, x.SortOrder }).HasDatabaseName("ix_artifact_post");
            b.HasOne<Post>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CopyDedupe>(b =>
        {
            b.ToTable("copy_dedupe", t =>
            {
                t.HasCheckConstraint("ck_copy_dedupe_hash_length", "octet_length(key_hash) = 32");
            });

            b.HasKey(x => x.KeyHash);
            b.Property(x => x.ExpiresAt).IsRequired();
            b.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_copy_dedupe_expiry");
        });

        modelBuilder.Entity<Comment>(b =>
        {
            b.ToTable("comment", t =>
            {
                t.HasCheckConstraint("ck_comment_body_length", "char_length(body_md) BETWEEN 2 AND 10000");
                t.HasCheckConstraint("ck_comment_moderation_status", "moderation_status IN ('pending','approved','rejected','hidden','deleted')");
                t.HasCheckConstraint("ck_comment_like_count", "like_count >= 0");
                t.HasCheckConstraint("ck_comment_version", "version > 0");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.BodyMd).IsRequired();
            b.Property(x => x.BodyHtml).IsRequired();
            b.Property(x => x.ModerationStatus).HasMaxLength(20).HasDefaultValue(ModerationStatuses.Pending);
            b.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.Id, x.PostId }).IsUnique().HasDatabaseName("uq_comment_id_post");
            b.HasIndex(x => new { x.PostId, x.ModerationStatus, x.CreatedAt }).HasDatabaseName("ix_comment_thread");
            b.HasIndex(x => new { x.PostId, x.ParentId, x.CreatedAt }).HasDatabaseName("ix_comment_parent");
            b.HasIndex(x => new { x.AuthorId, x.CreatedAt }).HasDatabaseName("ix_comment_author");
            b.HasIndex(x => x.PostId).IsUnique().HasFilter("is_accepted").HasDatabaseName("uq_comment_accepted");
            b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_comment_moderation_queue").HasFilter("moderation_status = 'pending' AND deleted_at IS NULL");
            b.HasOne<Post>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Comment>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}