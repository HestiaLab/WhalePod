using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class PostWriteServiceTests
{
    [DatabaseFact]
    public async Task Create_stores_pending_content_html_audit_images_and_author_visibility()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("post-author");
        var admin = ServiceTestSupport.NewUser("post-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var tag = ServiceTestSupport.NewTag();
        var upload = NewUpload(member.Id);
        db.Users.AddRange(member, admin);
        db.Categories.Add(category);
        db.Tags.Add(tag);
        db.MediaUploads.Add(upload);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var command = new PostCreateCommand(
            "在 Ubuntu 24.04 上复现实测问题",
            category.Id,
            "## 环境\n\n这是足够长的正文，包含 `code` 与链接。\n\n```bash\necho ok\n```",
            [tag.Slug],
            new EnvironmentInput("6.8.0-45-generic", Platforms.LinuxX64, "deepseek-flash"),
            [new ArtifactInput("shell", "repair.sh", "先备份", "echo repair")],
            [new PostImageInput(upload.Id, "测试图")]);

        var detail = await services.Posts.CreateAsync(
            member.Id,
            command,
            ServiceTestSupport.NewAudit(member.Id));

        Assert.Equal(ModerationStatuses.Pending, detail.ModerationStatus);
        Assert.Equal(1, detail.Version);
        Assert.Contains("<h2", detail.BodyHtml, StringComparison.Ordinal);
        Assert.Single(detail.Images);
        Assert.Equal("测试图", detail.Images[0].AltText);
        Assert.Single(detail.Artifacts);
        Assert.Equal(tag.Slug, Assert.Single(detail.Tags).Slug);
        Assert.Null(await services.Content.GetPostAsync(detail.Id));
        Assert.NotNull(await services.Content.GetPostForViewerAsync(detail.Id, member.Id, false));
        Assert.NotNull(await services.Content.GetPostForViewerAsync(detail.Id, admin.Id, true));

        db.ChangeTracker.Clear();
        var stored = await db.Posts.SingleAsync(post => post.Id == detail.Id);
        Assert.Equal(ModerationStatuses.Pending, stored.ModerationStatus);
        Assert.Equal(1, stored.Version);
        Assert.Equal("6.8.0-45-generic", stored.EnvKernel);
        var storedUpload = await db.MediaUploads.SingleAsync(candidate => candidate.Id == upload.Id);
        Assert.Equal(MediaUploadStatuses.Attached, storedUpload.Status);
        Assert.Equal(stored.Id, storedUpload.PostId);
        var audit = await db.AuditLogs.SingleAsync(log => log.TargetId == stored.Id && log.Action == "post.created");
        Assert.Equal(member.Id, audit.ActorId);
        Assert.Equal(32, audit.IpHash?.Length);
        Assert.DoesNotContain("这是足够长的正文", audit.MetadataJson.RootElement.GetRawText());

        var queueItem = await ServiceTestSupport.FindQueueItemAsync(
            services.Moderation,
            admin.Id,
            "post",
            stored.Id);
        Assert.NotNull(queueItem);
        Assert.Equal("post", queueItem.TargetType);
        Assert.Null(queueItem.AiSuggestion);
        Assert.Empty(queueItem.RiskHints);
    }

    [DatabaseFact]
    public async Task Create_enforces_account_state_for_pending_muted_banned_deleted_and_admin()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var pending = ServiceTestSupport.NewUser("post-pending", activated: false);
        var muted = ServiceTestSupport.NewUser(
            "post-muted",
            status: UserStatuses.Muted,
            statusReason: "测试禁言",
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(1));
        var banned = ServiceTestSupport.NewUser(
            "post-banned",
            status: UserStatuses.Banned,
            statusReason: "测试封禁");
        var deleted = ServiceTestSupport.NewUser("post-deleted", status: UserStatuses.Deleted);
        deleted.DeletedAt = DateTimeOffset.UtcNow;
        var member = ServiceTestSupport.NewUser("post-member");
        var admin = ServiceTestSupport.NewUser("post-admin-state", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(pending, muted, banned, deleted, member, admin);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var command = new PostCreateCommand(
            "状态校验测试帖子标题",
            category.Id,
            "这是用于账号状态校验的测试正文，长度足够。",
            null,
            null,
            null,
            null);

        var pendingError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Posts.CreateAsync(pending.Id, command, ServiceTestSupport.NewAudit(pending.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, pendingError.Code);
        Assert.Equal("activation_required", DetailsReason(pendingError));

        var mutedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Posts.CreateAsync(muted.Id, command, ServiceTestSupport.NewAudit(muted.Id)));
        Assert.Equal(ApiErrorCodes.AccountMuted, mutedError.Code);

        var bannedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Posts.CreateAsync(banned.Id, command, ServiceTestSupport.NewAudit(banned.Id)));
        Assert.Equal(ApiErrorCodes.AccountBanned, bannedError.Code);

        var deletedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Posts.CreateAsync(deleted.Id, command, ServiceTestSupport.NewAudit(deleted.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, deletedError.Code);
        Assert.Equal("account_deleted", DetailsReason(deletedError));

        var memberPost = await services.Posts.CreateAsync(
            member.Id,
            command,
            ServiceTestSupport.NewAudit(member.Id));
        Assert.Equal(ModerationStatuses.Pending, memberPost.ModerationStatus);

        var adminPost = await services.Posts.CreateAsync(
            admin.Id,
            command,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(ModerationStatuses.Pending, adminPost.ModerationStatus);
    }

    [DatabaseFact]
    public async Task Create_validates_fields_tags_environment_artifacts_and_image_ownership()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("post-validation");
        var other = ServiceTestSupport.NewUser("post-validation-other");
        var category = ServiceTestSupport.NewCategory();
        var homework = ServiceTestSupport.NewCategory("抄作业");
        homework.RequiresKernel = true;
        var hiddenTag = ServiceTestSupport.NewTag(TagStatuses.Hidden);
        var activeTag = ServiceTestSupport.NewTag();
        var foreignUpload = NewUpload(other.Id);
        var expiredUpload = NewUpload(member.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
        db.Users.AddRange(member, other);
        db.Categories.AddRange(category, homework);
        db.Tags.AddRange(hiddenTag, activeTag);
        db.MediaUploads.AddRange(foreignUpload, expiredUpload);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var audit = ServiceTestSupport.NewAudit(member.Id);

        await AssertValidationAsync(
            () => services.Posts.CreateAsync(member.Id, Command(category.Id, title: "太短"), audit),
            "title");
        await AssertValidationAsync(
            () => services.Posts.CreateAsync(
                member.Id,
                Command(category.Id, tags: ["a", "b", "c", "d", "e", "f", "g"]),
                audit),
            "tags");
        await AssertValidationAsync(
            () => services.Posts.CreateAsync(member.Id, Command(category.Id, tags: [hiddenTag.Slug]), audit),
            "tags");
        await AssertValidationAsync(
            () => services.Posts.CreateAsync(
                member.Id,
                Command(category.Id, environment: new EnvironmentInput(null, "plan9", null)),
                audit),
            "environment.platform");
        await AssertValidationAsync(
            () => services.Posts.CreateAsync(member.Id, Command(homework.Id), audit),
            "environment.kernelVersion");
        await AssertValidationAsync(
            () => services.Posts.CreateAsync(
                member.Id,
                Command(
                    category.Id,
                    artifacts:
                    [
                        new ArtifactInput("shell", "a", null, "a"),
                        new ArtifactInput("shell", "b", null, "b"),
                        new ArtifactInput("shell", "c", null, "c"),
                        new ArtifactInput("shell", "d", null, "d"),
                        new ArtifactInput("shell", "e", null, "e"),
                        new ArtifactInput("shell", "f", null, "f")
                    ]),
                audit),
            "artifacts");
        await AssertValidationAsync(
            () => services.Posts.CreateAsync(
                member.Id,
                Command(category.Id, images: [new PostImageInput(foreignUpload.Id, null)]),
                audit),
            "images");
        await AssertValidationAsync(
            () => services.Posts.CreateAsync(
                member.Id,
                Command(category.Id, images: [new PostImageInput(expiredUpload.Id, null)]),
                audit),
            "images");
    }

    [DatabaseFact]
    public async Task Markdown_sanitizes_raw_html_scripts_and_dangerous_urls()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("post-xss");
        var category = ServiceTestSupport.NewCategory();
        db.Users.Add(member);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var body = """
            ## 标题

            <script>alert('xss')</script>

            [坏链接](javascript:alert(1))
            [好链接](https://example.com/path)
            [站内链接](/posts/example)

            ![示意图](/media/example.png)

            <img src=x onerror=alert(1)>

            <a href="javascript:alert(1)" onclick="evil()">raw</a>
            """;
        var detail = await services.Posts.CreateAsync(
            member.Id,
            new PostCreateCommand(
                "XSS 清洗测试帖子标题",
                category.Id,
                body,
                null,
                null,
                null,
                null),
            ServiceTestSupport.NewAudit(member.Id));

        Assert.DoesNotContain("<script", detail.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", detail.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a href=\"javascript:", detail.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=\"javascript:", detail.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", detail.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("rel=\"nofollow noopener noreferrer\"", detail.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("https://example.com/path", detail.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/posts/example\"", detail.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("src=\"/media/example.png\"", detail.BodyHtml, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Update_uses_version_requires_admin_reason_and_deletes_are_audited()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("post-edit-author");
        var admin = ServiceTestSupport.NewUser("post-edit-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(author, admin);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var created = await services.Posts.CreateAsync(
            author.Id,
            Command(category.Id, title: "并发编辑测试帖子标题"),
            ServiceTestSupport.NewAudit(author.Id));

        var updated = await services.Posts.UpdateAsync(
            author.Id,
            created.Id,
            1,
            new PostUpdateCommand(
                "编辑后的帖子标题长度足够",
                null,
                "编辑后的正文内容仍然足够长，用于等待审核。",
                null,
                null,
                null,
                null,
                null,
                null),
            ServiceTestSupport.NewAudit(author.Id));
        Assert.Equal(2, updated.Version);
        Assert.Equal(ModerationStatuses.Pending, updated.ModerationStatus);

        var stale = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Posts.UpdateAsync(
                author.Id,
                created.Id,
                1,
                new PostUpdateCommand(
                    "再次编辑帖子标题长度足够",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
                ServiceTestSupport.NewAudit(author.Id)));
        Assert.Equal(ApiErrorCodes.Conflict, stale.Code);

        var missingReason = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Posts.UpdateAsync(
                admin.Id,
                created.Id,
                2,
                new PostUpdateCommand(
                    "管理员编辑标题长度足够",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, missingReason.Code);

        var adminUpdated = await services.Posts.UpdateAsync(
            admin.Id,
            created.Id,
            2,
            new PostUpdateCommand(
                "管理员编辑标题长度足够",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                "修正误导性标题"),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(3, adminUpdated.Version);

        var outsider = ServiceTestSupport.NewUser("post-outsider");
        db.Users.Add(outsider);
        await db.SaveChangesAsync();
        var forbidden = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Posts.DeleteAsync(
                outsider.Id,
                created.Id,
                ServiceTestSupport.NewAudit(outsider.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, forbidden.Code);

        await services.Posts.DeleteAsync(
            author.Id,
            created.Id,
            ServiceTestSupport.NewAudit(author.Id));
        db.ChangeTracker.Clear();
        var deleted = await db.Posts.SingleAsync(post => post.Id == created.Id);
        Assert.Equal(ModerationStatuses.Deleted, deleted.ModerationStatus);
        Assert.NotNull(deleted.DeletedAt);
        Assert.Null(await services.Content.GetPostAsync(created.Id));
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "post.deleted" && log.TargetId == created.Id && log.IpHash != null));
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "post.updated"
            && log.TargetId == created.Id
            && log.ActorId == admin.Id
            && log.MetadataJson.RootElement.GetProperty("reason").GetString() == "修正误导性标题"));
    }

    [DatabaseFact]
    public async Task Post_images_enforce_count_status_ownership_duplicates_and_keep_rules()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("post-images");
        var category = ServiceTestSupport.NewCategory();
        var seedPost = ServiceTestSupport.NewPost(member.Id, category.Id);
        var uploads = Enumerable.Range(0, 11)
            .Select(_ => NewUpload(member.Id))
            .ToList();
        var attachedUpload = NewUpload(member.Id);
        attachedUpload.Status = MediaUploadStatuses.Attached;
        attachedUpload.PostId = seedPost.Id;
        attachedUpload.AttachedAt = DateTimeOffset.UtcNow;
        var deletedUpload = NewUpload(member.Id);
        deletedUpload.Status = MediaUploadStatuses.Deleted;
        db.Users.Add(member);
        db.Categories.Add(category);
        db.Posts.Add(seedPost);
        db.MediaUploads.AddRange(uploads);
        db.MediaUploads.AddRange(attachedUpload, deletedUpload);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var audit = ServiceTestSupport.NewAudit(member.Id);

        await AssertValidationAsync(
            () => services.Posts.CreateAsync(
                member.Id,
                Command(
                    category.Id,
                    images: uploads.Select(upload => new PostImageInput(upload.Id, null)).ToList()),
                audit),
            "images");

        await AssertValidationAsync(
            () => services.Posts.CreateAsync(
                member.Id,
                Command(
                    category.Id,
                    images:
                    [
                        new PostImageInput(uploads[0].Id, null),
                        new PostImageInput(uploads[0].Id, "重复")
                    ]),
                audit),
            "images");

        await AssertValidationAsync(
            () => services.Posts.CreateAsync(
                member.Id,
                Command(category.Id, images: [new PostImageInput(attachedUpload.Id, null)]),
                audit),
            "images");

        await AssertValidationAsync(
            () => services.Posts.CreateAsync(
                member.Id,
                Command(category.Id, images: [new PostImageInput(deletedUpload.Id, null)]),
                audit),
            "images");

        var created = await services.Posts.CreateAsync(
            member.Id,
            Command(
                category.Id,
                images: uploads.Take(10).Select(upload => new PostImageInput(upload.Id, "图片")).ToList()),
            audit);
        Assert.Equal(10, created.Images.Count);

        db.ChangeTracker.Clear();
        Assert.Equal(10, await db.PostImages.CountAsync(image => image.PostId == created.Id));
        Assert.All(
            await db.MediaUploads.AsNoTracking()
                .Where(upload => uploads.Take(10).Select(candidate => candidate.Id).Contains(upload.Id))
                .ToListAsync(),
            upload =>
            {
                Assert.Equal(MediaUploadStatuses.Attached, upload.Status);
                Assert.Equal(created.Id, upload.PostId);
            });

        await AssertValidationAsync(
            () => services.Posts.CreateAsync(
                member.Id,
                Command(
                    category.Id,
                    title: "复用已经使用过的图片上传记录",
                    images: [new PostImageInput(uploads[0].Id, null)]),
                audit),
            "images");

        var imagesBefore = await db.PostImages
            .AsNoTracking()
            .Where(image => image.PostId == created.Id)
            .OrderBy(image => image.SortOrder)
            .Select(image => new { image.Id, image.UploadId })
            .ToListAsync();
        var keepImageIds = imagesBefore.Take(5).Select(image => image.Id).ToList();
        var removedUploadIds = imagesBefore.Skip(5).Select(image => image.UploadId).ToList();
        var extraUpload = NewUpload(member.Id);
        db.MediaUploads.Add(extraUpload);
        await db.SaveChangesAsync();

        var updated = await services.Posts.UpdateAsync(
            member.Id,
            created.Id,
            created.Version,
            new PostUpdateCommand(
                null,
                null,
                null,
                null,
                null,
                null,
                keepImageIds,
                [new PostImageInput(extraUpload.Id, "新增图片")],
                null),
            audit);
        Assert.Equal(6, updated.Images.Count);
        Assert.Equal("新增图片", updated.Images[^1].AltText);

        db.ChangeTracker.Clear();
        Assert.Equal(5, await db.PostImages.CountAsync(image =>
            image.PostId == created.Id && keepImageIds.Contains(image.Id)));
        Assert.Equal(5, await db.MediaUploads.CountAsync(upload =>
            removedUploadIds.Contains(upload.Id) && upload.Status == MediaUploadStatuses.Deleted));
        Assert.Equal(
            MediaUploadStatuses.Attached,
            (await db.MediaUploads.SingleAsync(upload => upload.Id == extraUpload.Id)).Status);

        await AssertValidationAsync(
            () => services.Posts.UpdateAsync(
                member.Id,
                created.Id,
                updated.Version,
                new PostUpdateCommand(
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [keepImageIds[0], keepImageIds[0]],
                    null,
                    null),
                audit),
            "keepImageIds");

        var overflowUploads = Enumerable.Range(0, 5)
            .Select(_ => NewUpload(member.Id))
            .ToList();
        db.MediaUploads.AddRange(overflowUploads);
        await db.SaveChangesAsync();
        var currentImageIds = await db.PostImages
            .AsNoTracking()
            .Where(image => image.PostId == created.Id)
            .Select(image => image.Id)
            .ToListAsync();
        await AssertValidationAsync(
            () => services.Posts.UpdateAsync(
                member.Id,
                created.Id,
                updated.Version,
                new PostUpdateCommand(
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    currentImageIds,
                    overflowUploads.Select(upload => new PostImageInput(upload.Id, null)).ToList(),
                    null),
                audit),
            "images");
    }

    private static PostCreateCommand Command(
        Guid categoryId,
        string title = "字段校验测试帖子标题",
        IReadOnlyList<string>? tags = null,
        EnvironmentInput? environment = null,
        IReadOnlyList<ArtifactInput>? artifacts = null,
        IReadOnlyList<PostImageInput>? images = null)
    {
        return new PostCreateCommand(
            title,
            categoryId,
            "这是用于字段校验的测试正文，长度足够。",
            tags,
            environment,
            artifacts,
            images);
    }

    private static MediaUpload NewUpload(Guid ownerId, DateTimeOffset? expiresAt = null)
    {
        var suffix = ServiceTestSupport.NewSuffix();
        return new MediaUpload
        {
            OwnerId = ownerId,
            Url = $"/media/{suffix}.png",
            Width = 640,
            Height = 480,
            MimeType = "image/png",
            ByteSize = 128,
            Status = MediaUploadStatuses.Temp,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1)
        };
    }

    private static async Task AssertValidationAsync(Func<Task> action, string field)
    {
        var error = await Assert.ThrowsAsync<ApiProblemException>(action);
        Assert.Equal(ApiErrorCodes.ValidationFailed, error.Code);
        var details = JsonSerializer.Serialize(error.Details);
        Assert.Contains(field, details, StringComparison.Ordinal);
    }

    private static string? DetailsReason(ApiProblemException exception)
    {
        return exception.Details?.GetType().GetProperty("reason")?.GetValue(exception.Details) as string;
    }
}