using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using WhalePod.Domain.Content;
using WhalePod.Domain.Moderation;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Tests.Unit;

public sealed class PersistenceModelTests
{
    private static WhalePodDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WhalePodDbContext>()
            .UseNpgsql("Host=localhost;Database=whalepod;Username=whalepod")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new WhalePodDbContext(options);
    }

    [Fact]
    public void Model_contains_all_p0_tables()
    {
        using var db = CreateContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var tables = model.GetEntityTypes().Select(x => x.GetTableName()).ToHashSet();

        Assert.Equal(33, tables.Count);
        Assert.Contains("user", tables);
        Assert.Contains("post", tables);
        Assert.Contains("comment", tables);
        Assert.Contains("media_upload", tables);
        Assert.Contains("appeal", tables);
        Assert.Contains("email_suppression", tables);
        Assert.Contains("moderation_check", tables);
        Assert.Contains("sensitive_word", tables);
        Assert.Contains("copy_dedupe", tables);
        Assert.Contains("moderation_suggestion", tables);
    }

    [Fact]
    public void User_identity_columns_and_unique_indexes_are_configured()
    {
        using var db = CreateContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var user = model.FindEntityType(typeof(WhalePod.Domain.Accounts.User))!;
        var githubId = user.FindProperty(nameof(WhalePod.Domain.Accounts.User.GitHubId))!;
        var hmac = user.FindProperty(nameof(WhalePod.Domain.Accounts.User.GitHubIdHmac))!;

        Assert.Equal("github_id", githubId.GetColumnName());
        Assert.Equal("github_id_hmac", hmac.GetColumnName());

        var handleIndex = user.GetIndexes().Single(x => x.GetDatabaseName() == "uq_user_handle");
        Assert.True(handleIndex.IsUnique);

        var hmacIndex = user.GetIndexes().Single(x => x.GetDatabaseName() == "uq_user_github_id_hmac");
        Assert.True(hmacIndex.IsUnique);
        Assert.Contains("github_id_hmac IS NOT NULL", hmacIndex.GetFilter());
    }

    [Fact]
    public void Post_feed_and_moderation_indexes_are_partial()
    {
        using var db = CreateContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var post = model.FindEntityType(typeof(Post))!;

        var active = post.GetIndexes().Single(x => x.GetDatabaseName() == "ix_post_feed_active");
        Assert.Contains("moderation_status = 'approved'", active.GetFilter());
        Assert.Contains("deleted_at IS NULL", active.GetFilter());

        var pending = post.GetIndexes().Single(x => x.GetDatabaseName() == "ix_post_moderation_queue");
        Assert.Contains("moderation_status = 'pending'", pending.GetFilter());

        var accepted = post.GetIndexes().Single(x => x.GetDatabaseName() == "uq_post_accepted_comment");
        Assert.True(accepted.IsUnique);
    }

    [Fact]
    public void Comment_acceptance_and_task_lease_uniques_are_configured()
    {
        using var db = CreateContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var comment = model.FindEntityType(typeof(Comment))!;
        var accepted = comment.GetIndexes().Single(x => x.GetDatabaseName() == "uq_comment_accepted");
        Assert.True(accepted.IsUnique);
        Assert.Equal("is_accepted", accepted.GetFilter());

        var task = model.FindEntityType(typeof(ModerationTask))!;
        var active = task.GetIndexes().Single(x => x.GetDatabaseName() == "uq_moderation_task_active");
        Assert.True(active.IsUnique);
        Assert.Contains("status IN ('queued','claimed','suggested','escalated')", active.GetFilter());
        Assert.Equal("content_hash", task.FindProperty(nameof(ModerationTask.ContentHash))!.GetColumnName());
    }

    [Fact]
    public void Email_and_notification_dedupe_indexes_are_configured()
    {
        using var db = CreateContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var email = model.FindEntityType(typeof(WhalePod.Domain.Communications.EmailOutbox))!;
        var dedupe = email.GetIndexes().Single(x => x.GetDatabaseName() == "uq_email_outbox_dedupe");
        Assert.True(dedupe.IsUnique);
        Assert.Contains("to_address", dedupe.Properties.Select(x => x.GetColumnName()));
        Assert.Contains("dedupe_key", dedupe.Properties.Select(x => x.GetColumnName()));

        var notification = model.FindEntityType(typeof(WhalePod.Domain.Interactions.Notification))!;
        var notificationDedupe = notification.GetIndexes().Single(x => x.GetDatabaseName() == "uq_notification_dedupe");
        Assert.True(notificationDedupe.IsUnique);
        Assert.Contains("dedupe_key IS NOT NULL", notificationDedupe.GetFilter());
    }

    [Fact]
    public void Critical_check_constraints_are_present()
    {
        using var db = CreateContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var post = model.FindEntityType(typeof(Post))!;
        var checks = post.GetCheckConstraints().Select(x => x.Name).ToHashSet();

        Assert.Contains("ck_post_solved", checks);
        Assert.Contains("ck_post_counts", checks);
        Assert.Contains("ck_post_env_platform", checks);

        var user = model.FindEntityType(typeof(WhalePod.Domain.Accounts.User))!;
        Assert.Contains("ck_user_github_hmac_length", user.GetCheckConstraints().Select(x => x.Name));
    }

    [Fact]
    public void Service_account_scopes_use_postgres_text_array()
    {
        using var db = CreateContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var account = model.FindEntityType(typeof(WhalePod.Domain.Platform.ServiceAccount))!;
        var scopes = account.FindProperty(nameof(WhalePod.Domain.Platform.ServiceAccount.Scopes))!;

        Assert.Equal("text[]", scopes.GetColumnType());
        Assert.Contains("ck_service_account_scopes", account.GetCheckConstraints().Select(x => x.Name));
    }
}