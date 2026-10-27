using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;

namespace WhalePod.Infrastructure.Persistence;

public sealed partial class WhalePodDbContext
{
    private static void ConfigureAccounts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("user", t =>
            {
                t.HasCheckConstraint("ck_user_trust_level", "trust_level BETWEEN 0 AND 4");
                t.HasCheckConstraint("ck_user_handle", "handle ~ '^[a-z0-9][a-z0-9-]*[a-z0-9]$' AND char_length(handle) BETWEEN 3 AND 32");
                t.HasCheckConstraint("ck_user_status", "status IN ('active','muted','banned','deleted')");
                t.HasCheckConstraint("ck_user_role", "role IN ('member','admin')");
                t.HasCheckConstraint("ck_user_status_reason", "status NOT IN ('muted','banned') OR (status_reason IS NOT NULL AND length(status_reason) > 0)");
                t.HasCheckConstraint("ck_user_status_expiry", "status <> 'muted' OR status_expires_at IS NOT NULL");
                t.HasCheckConstraint("ck_user_deleted_at", "status <> 'deleted' OR deleted_at IS NOT NULL");
                t.HasCheckConstraint("ck_user_github_hmac_length", "github_id_hmac IS NULL OR octet_length(github_id_hmac) = 32");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Handle).HasMaxLength(32).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(80).IsRequired();
            b.Property(x => x.Bio).HasMaxLength(500);
            b.Property(x => x.AvatarUrl);
            b.Property(x => x.GitHubAvatarUrl).HasMaxLength(500).HasColumnName("github_avatar_url");
            b.Property(x => x.GitHubId).HasColumnName("github_id");
            b.Property(x => x.GitHubIdHmac).HasColumnName("github_id_hmac");
            b.Property(x => x.Email).HasColumnType("citext");
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(UserStatuses.Active);
            b.Property(x => x.StatusReason).HasMaxLength(500);
            b.Property(x => x.Role).HasMaxLength(20).HasDefaultValue(UserRoles.Member);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            b.HasIndex(x => x.Handle).IsUnique().HasDatabaseName("uq_user_handle");
            b.HasIndex(x => x.GitHubId).IsUnique().HasFilter("github_id IS NOT NULL").HasDatabaseName("uq_user_github_id");
            b.HasIndex(x => x.GitHubIdHmac).IsUnique().HasFilter("github_id_hmac IS NOT NULL").HasDatabaseName("uq_user_github_id_hmac");
            b.HasIndex(x => x.Status).HasDatabaseName("ix_user_status");
            b.HasIndex(x => x.TrustLevel).HasDatabaseName("ix_user_trust_level");
            b.HasIndex(x => x.ActivatedAt).HasDatabaseName("ix_user_activated_at");
        });

        modelBuilder.Entity<ExternalLogin>(b =>
        {
            b.ToTable("external_login", t =>
            {
                t.HasCheckConstraint("ck_external_login_provider", "provider = 'github'");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Provider).HasMaxLength(20).HasDefaultValue("github");
            b.Property(x => x.ProviderUserId).HasMaxLength(100).IsRequired();
            b.Property(x => x.ProviderLogin).HasMaxLength(100);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.Provider, x.ProviderUserId }).IsUnique().HasDatabaseName("uq_external_login_provider");
            b.HasIndex(x => new { x.UserId, x.Provider }).IsUnique().HasDatabaseName("uq_external_login_user_provider");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InviteCode>(b =>
        {
            b.ToTable("invite_code", t =>
            {
                t.HasCheckConstraint("ck_invite_code_hash_length", "octet_length(code_hash) = 32");
                t.HasCheckConstraint("ck_invite_code_status", "status IN ('active','disabled')");
                t.HasCheckConstraint("ck_invite_code_uses", "max_uses BETWEEN 1 AND 1000 AND used_count >= 0 AND used_count <= max_uses");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.CodeHash).IsRequired();
            b.Property(x => x.CodeHint).HasMaxLength(8);
            b.Property(x => x.MaxUses).HasDefaultValue(1);
            b.Property(x => x.UsedCount).HasDefaultValue(0);
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(InviteCodeStatuses.Active);
            b.Property(x => x.Note).HasMaxLength(200);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.CodeHash).IsUnique().HasDatabaseName("uq_invite_code_hash");
            b.HasIndex(x => new { x.Status, x.ExpiresAt }).HasDatabaseName("ix_invite_code_admin");
            b.HasIndex(x => x.CreatedBy).HasDatabaseName("ix_invite_code_created_by");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InviteRedemption>(b =>
        {
            b.ToTable("invite_redemption");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.UserId).IsUnique().HasDatabaseName("uq_invite_redemption_user");
            b.HasIndex(x => x.InviteCodeId).HasDatabaseName("ix_invite_redemption_code");
            b.HasIndex(x => x.InvitedBy).HasDatabaseName("ix_invite_redemption_inviter");
            b.HasOne<InviteCode>().WithMany().HasForeignKey(x => x.InviteCodeId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.InvitedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Device>(b =>
        {
            b.ToTable("device", t =>
            {
                t.HasCheckConstraint("ck_device_status", "status IN ('active','revoked')");
                t.HasCheckConstraint("ck_device_platform", "platform IN ('win32-x64','win32-arm64','darwin-x64','darwin-arm64','linux-x64','linux-arm64')");
                t.HasCheckConstraint("ck_device_fingerprint", "fingerprint ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_device_kernel_length", "char_length(kernel_version) BETWEEN 1 AND 64");
                t.HasCheckConstraint("ck_device_label_length", "label IS NULL OR char_length(label) <= 40");
                t.HasCheckConstraint("ck_device_revoke_reason", "status <> 'revoked' OR revoked_at IS NOT NULL");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PublicKey).IsRequired();
            b.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            b.Property(x => x.KernelVersion).HasMaxLength(64).IsRequired();
            b.Property(x => x.Platform).HasMaxLength(30).IsRequired();
            b.Property(x => x.Label).HasMaxLength(40);
            b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(DeviceStatuses.Active);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.LastVerifiedAt).HasDefaultValueSql("now()");
            b.Property(x => x.RevokeReason).HasMaxLength(200);
            b.HasIndex(x => x.PublicKey).IsUnique().HasDatabaseName("uq_device_public_key");
            b.HasIndex(x => x.Fingerprint).IsUnique().HasDatabaseName("uq_device_fingerprint");
            b.HasIndex(x => new { x.UserId, x.Status }).HasDatabaseName("ix_device_user_status");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.RevokedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeviceChallenge>(b =>
        {
            b.ToTable("device_challenge", t =>
            {
                t.HasCheckConstraint("ck_device_challenge_hash_length", "octet_length(challenge_hash) = 32");
                t.HasCheckConstraint("ck_device_challenge_attempts", "attempt_count >= 0");
                t.HasCheckConstraint("ck_device_challenge_expiry", "expires_at > created_at");
            });

            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ChallengeHash).IsRequired();
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.ChallengeHash).IsUnique().HasDatabaseName("uq_device_challenge_hash");
            b.HasIndex(x => new { x.UserId, x.ExpiresAt }).HasDatabaseName("ix_challenge_pending").HasFilter("used_at IS NULL");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}