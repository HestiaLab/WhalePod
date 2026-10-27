using Microsoft.EntityFrameworkCore;

using WhalePod.Domain.Accounts;
using WhalePod.Domain.Communications;
using WhalePod.Domain.Content;
using WhalePod.Domain.Interactions;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;

namespace WhalePod.Infrastructure.Persistence;

public sealed partial class WhalePodDbContext : DbContext
{
    public WhalePodDbContext(DbContextOptions<WhalePodDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();
    public DbSet<InviteRedemption> InviteRedemptions => Set<InviteRedemption>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceChallenge> DeviceChallenges => Set<DeviceChallenge>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<PostImage> PostImages => Set<PostImage>();
    public DbSet<MediaUpload> MediaUploads => Set<MediaUpload>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PostTag> PostTags => Set<PostTag>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<CopyDedupe> CopyDedupes => Set<CopyDedupe>();
    public DbSet<PostLike> PostLikes => Set<PostLike>();
    public DbSet<CommentLike> CommentLikes => Set<CommentLike>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ModerationAction> ModerationActions => Set<ModerationAction>();
    public DbSet<ModerationTask> ModerationTasks => Set<ModerationTask>();
    public DbSet<ModerationSuggestion> ModerationSuggestions => Set<ModerationSuggestion>();
    public DbSet<Appeal> Appeals => Set<Appeal>();
    public DbSet<ModerationCheck> ModerationChecks => Set<ModerationCheck>();
    public DbSet<SensitiveWord> SensitiveWords => Set<SensitiveWord>();
    public DbSet<EmailOutbox> EmailOutbox => Set<EmailOutbox>();
    public DbSet<EmailPreference> EmailPreferences => Set<EmailPreference>();
    public DbSet<EmailSuppression> EmailSuppressions => Set<EmailSuppression>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();
    public DbSet<ServiceToken> ServiceTokens => Set<ServiceToken>();
    public DbSet<AgentInstance> AgentInstances => Set<AgentInstance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.HasPostgresExtension("pg_trgm");

        ConfigureAccounts(modelBuilder);
        ConfigureContent(modelBuilder);
        ConfigureInteractions(modelBuilder);
        ConfigureModeration(modelBuilder);
        ConfigureCommunications(modelBuilder);
        ConfigurePlatform(modelBuilder);
    }
}