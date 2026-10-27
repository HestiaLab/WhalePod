using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record EnvironmentInput(string? KernelVersion, string? Platform, string? Model);

public sealed record ArtifactInput(string? Kind, string? Name, string? Note, string? Content);

public sealed record PostImageInput(Guid Id, string? AltText);

public sealed record PostCreateCommand(
    string? Title,
    Guid CategoryId,
    string? BodyMd,
    IReadOnlyList<string>? Tags,
    EnvironmentInput? Environment,
    IReadOnlyList<ArtifactInput>? Artifacts,
    IReadOnlyList<PostImageInput>? Images);

public sealed record PostUpdateCommand(
    string? Title,
    Guid? CategoryId,
    string? BodyMd,
    IReadOnlyList<string>? Tags,
    EnvironmentInput? Environment,
    IReadOnlyList<ArtifactInput>? Artifacts,
    IReadOnlyList<Guid>? KeepImageIds,
    IReadOnlyList<PostImageInput>? Images,
    string? Reason);

public sealed class PostWriteService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    PublicContentService publicContent,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> AllowedPlatforms =
    [
        Platforms.Win32X64,
        Platforms.Win32Arm64,
        Platforms.DarwinX64,
        Platforms.DarwinArm64,
        Platforms.LinuxX64,
        Platforms.LinuxArm64
    ];

    private static readonly HashSet<string> AllowedArtifactKinds =
    [
        ArtifactKinds.Patch,
        ArtifactKinds.Yaml,
        ArtifactKinds.Json,
        ArtifactKinds.Bundle,
        ArtifactKinds.Markdown,
        ArtifactKinds.Shell
    ];

    private static readonly Regex KernelPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._+~()/ -]{0,63}$",
        RegexOptions.Compiled);

    private static readonly Regex TagSlugPattern = new(
        @"^[a-z0-9][a-z0-9-]*$",
        RegexOptions.Compiled);

    public async Task<PostDetailView> CreateAsync(
        Guid actorId,
        PostCreateCommand command,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorGuard.RequireWriteAccessAsync(
            actorId,
            ActorWriteScope.Content,
            cancellationToken);
        var now = timeProvider.GetUtcNow();

        var title = NormalizeTitle(command.Title);
        var bodyMd = NormalizeBody(command.BodyMd);
        var category = await LoadActiveCategoryAsync(command.CategoryId, "categoryId", cancellationToken);
        var environment = NormalizeEnvironment(command.Environment, category, "environment");
        var tagIds = await LoadActiveTagIdsAsync(command.Tags, "tags", cancellationToken);
        var artifacts = NormalizeArtifacts(command.Artifacts, "artifacts");
        var images = await LoadUploadsAsync(actorId, command.Images, "images", now, cancellationToken);
        var rendered = MarkdownRenderer.Render(bodyMd);

        var post = new Post
        {
            AuthorId = actor.Id,
            CategoryId = category.Id,
            Title = title,
            BodyMd = bodyMd,
            BodyHtml = rendered.Html,
            Excerpt = rendered.Excerpt,
            EnvKernel = environment.Kernel,
            EnvPlatform = environment.Platform,
            EnvModel = environment.Model,
            ModerationStatus = ModerationStatuses.Pending,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
            LastActivityAt = now
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.Posts.Add(post);
        foreach (var tagId in tagIds)
        {
            db.PostTags.Add(new PostTag { PostId = post.Id, TagId = tagId, CreatedAt = now });
        }

        AddArtifacts(post.Id, artifacts, now);
        AddImages(post.Id, images, now);

        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "post.created",
            "post",
            post.Id,
            new { moderationStatus = post.ModerationStatus, categoryId = post.CategoryId },
            now));

        await db.SaveChangesAsync(cancellationToken);
        var detail = await publicContent.GetPostForViewerAsync(
            post.Id,
            actorId,
            actor.Role == UserRoles.Admin,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return detail ?? throw new InvalidOperationException("Created post could not be reloaded.");
    }

    public async Task<PostDetailView> UpdateAsync(
        Guid actorId,
        Guid postId,
        int expectedVersion,
        PostUpdateCommand command,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorGuard.RequireWriteAccessAsync(
            actorId,
            ActorWriteScope.Content,
            cancellationToken);

        if (!HasPostChanges(command))
        {
            throw ApiProblemException.Validation("request", "至少需要提供一个可更新字段。");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var post = await db.Posts
            .SingleOrDefaultAsync(candidate => candidate.Id == postId && candidate.DeletedAt == null, cancellationToken);
        if (post is null)
        {
            throw ApiProblemException.NotFound("帖子不存在或不可见。");
        }

        var isAdmin = actor.Role == UserRoles.Admin;
        var isOwner = post.AuthorId == actorId;
        if (!isOwner && !isAdmin)
        {
            throw ApiProblemException.Forbidden("not_owner", "只能编辑自己的帖子。");
        }

        if (!isAdmin && post.ModerationStatus is ModerationStatuses.Hidden or ModerationStatuses.Deleted)
        {
            throw ApiProblemException.NotFound("帖子不存在或不可见。");
        }

        if (post.Version != expectedVersion)
        {
            throw VersionConflict(expectedVersion, post.Version);
        }

        if (isAdmin && !isOwner && string.IsNullOrWhiteSpace(command.Reason))
        {
            throw ApiProblemException.Validation("reason", "管理员编辑他人帖子时必须填写原因。");
        }

        if (isAdmin && !isOwner && RuneHelper.Count(command.Reason) > 500)
        {
            throw ApiProblemException.Validation("reason", "原因不能超过 500 个字符。");
        }

        if (command.Title is not null)
        {
            post.Title = NormalizeTitle(command.Title);
        }

        if (command.CategoryId is not null)
        {
            var category = await LoadActiveCategoryAsync(command.CategoryId.Value, "categoryId", cancellationToken);
            if (category.Id != post.CategoryId)
            {
                post.CategoryId = category.Id;
            }
        }

        if (command.BodyMd is not null)
        {
            var bodyMd = NormalizeBody(command.BodyMd);
            var rendered = MarkdownRenderer.Render(bodyMd);
            post.BodyMd = bodyMd;
            post.BodyHtml = rendered.Html;
            post.Excerpt = rendered.Excerpt;
        }

        if (command.Environment is not null)
        {
            var category = await LoadCategoryAsync(post.CategoryId, cancellationToken);
            var environment = NormalizeEnvironment(command.Environment, category, "environment");
            post.EnvKernel = environment.Kernel;
            post.EnvPlatform = environment.Platform;
            post.EnvModel = environment.Model;
        }
        else if (command.CategoryId is not null)
        {
            var category = await LoadCategoryAsync(post.CategoryId, cancellationToken);
            if (category.Name == "抄作业" && string.IsNullOrWhiteSpace(post.EnvKernel))
            {
                throw ApiProblemException.Validation("environment.kernelVersion", "“抄作业”板块必须填写内核版本。");
            }
        }

        if (command.Tags is not null)
        {
            var tagIds = await LoadActiveTagIdsAsync(command.Tags, "tags", cancellationToken);
            var existing = await db.PostTags
                .Where(postTag => postTag.PostId == postId)
                .ToListAsync(cancellationToken);
            db.PostTags.RemoveRange(existing);
            foreach (var tagId in tagIds)
            {
                db.PostTags.Add(new PostTag { PostId = postId, TagId = tagId, CreatedAt = now });
            }
        }

        if (command.Artifacts is not null)
        {
            var artifacts = NormalizeArtifacts(command.Artifacts, "artifacts");
            var existing = await db.Artifacts
                .Where(artifact => artifact.PostId == postId)
                .ToListAsync(cancellationToken);
            db.Artifacts.RemoveRange(existing);
            AddArtifacts(postId, artifacts, now);
        }

        if (command.KeepImageIds is not null || command.Images is not null)
        {
            await ReplaceImagesAsync(actorId, post, command.KeepImageIds, command.Images, now, cancellationToken);
        }

        if (!isAdmin)
        {
            if (post.ModerationStatus is ModerationStatuses.Pending or ModerationStatuses.Rejected
                or ModerationStatuses.Approved)
            {
                post.ModerationStatus = ModerationStatuses.Pending;
            }
        }

        post.Version++;
        post.UpdatedAt = now;

        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "post.updated",
            "post",
            post.Id,
            new
            {
                version = post.Version,
                moderationStatus = post.ModerationStatus,
                editedByAdmin = isAdmin && !isOwner,
                reason = isAdmin && !isOwner ? command.Reason : null
            },
            now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var currentVersion = await db.Posts
                .AsNoTracking()
                .Where(candidate => candidate.Id == postId)
                .Select(candidate => (int?)candidate.Version)
                .SingleOrDefaultAsync(cancellationToken);
            throw VersionConflict(expectedVersion, currentVersion);
        }

        var detail = await publicContent.GetPostForViewerAsync(post.Id, actorId, isAdmin, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return detail ?? throw ApiProblemException.NotFound("帖子不存在或不可见。");
    }

    public async Task<Guid> DeleteAsync(
        Guid actorId,
        Guid postId,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireWriteAccessAsync(actorId, ActorWriteScope.Content, cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var post = await db.Posts
            .SingleOrDefaultAsync(candidate => candidate.Id == postId && candidate.DeletedAt == null, cancellationToken);
        if (post is null)
        {
            throw ApiProblemException.NotFound("帖子不存在或不可见。");
        }

        if (post.AuthorId != actorId)
        {
            throw ApiProblemException.Forbidden("not_owner", "只能删除自己的帖子。");
        }

        post.ModerationStatus = ModerationStatuses.Deleted;
        post.DeletedAt = now;
        post.Version++;
        post.UpdatedAt = now;

        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "post.deleted",
            "post",
            post.Id,
            new { version = post.Version },
            now));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return post.Id;
    }

    private async Task<Category> LoadActiveCategoryAsync(
        Guid categoryId,
        string field,
        CancellationToken cancellationToken)
    {
        if (categoryId == Guid.Empty)
        {
            throw ApiProblemException.Validation(field, "板块不能为空。");
        }

        var category = await db.Categories
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == categoryId, cancellationToken);
        if (category is null || category.Status != CategoryStatuses.Active)
        {
            throw ApiProblemException.Validation(field, "板块不存在或已停用。");
        }

        return category;
    }

    private async Task<Category> LoadCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        return await db.Categories
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == categoryId, cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> LoadActiveTagIdsAsync(
        IReadOnlyList<string>? tags,
        string field,
        CancellationToken cancellationToken)
    {
        if (tags is null || tags.Count == 0)
        {
            return [];
        }

        if (tags.Count > 6)
        {
            throw ApiProblemException.Validation(field, "标签最多 6 个。");
        }

        var normalized = new List<string>(tags.Count);
        foreach (var raw in tags)
        {
            var slug = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (!TagSlugPattern.IsMatch(slug) || slug.Length > 40)
            {
                throw ApiProblemException.Validation(field, "标签 slug 格式无效。");
            }

            if (normalized.Contains(slug, StringComparer.Ordinal))
            {
                throw ApiProblemException.Validation(field, "标签不能重复。");
            }

            normalized.Add(slug);
        }

        var tagRows = await db.Tags
            .AsNoTracking()
            .Where(tag => normalized.Contains(tag.Slug))
            .Select(tag => new { tag.Id, tag.Slug, tag.Status })
            .ToListAsync(cancellationToken);
        if (tagRows.Count != normalized.Count
            || tagRows.Any(tag => tag.Status != TagStatuses.Active))
        {
            throw ApiProblemException.Validation(field, "标签不存在或已隐藏。");
        }

        var bySlug = tagRows.ToDictionary(tag => tag.Slug, tag => tag.Id, StringComparer.Ordinal);
        return normalized.Select(slug => bySlug[slug]).ToList();
    }

    private static string NormalizeTitle(string? title)
    {
        var normalized = (title ?? string.Empty).Trim();
        var count = RuneHelper.Count(normalized);
        if (count is < 8 or > 200)
        {
            throw ApiProblemException.Validation("title", "标题长度必须为 8–200 个字符。");
        }

        if (normalized.Any(character => char.IsControl(character)))
        {
            throw ApiProblemException.Validation("title", "标题不能包含控制字符。");
        }

        return normalized;
    }

    private static string NormalizeBody(string? bodyMd)
    {
        var body = bodyMd ?? string.Empty;
        var count = RuneHelper.Count(body);
        if (count is < 10 or > 60_000 || string.IsNullOrWhiteSpace(body))
        {
            throw ApiProblemException.Validation("bodyMd", "正文长度必须为 10–60,000 个字符。");
        }

        if (body.Contains('\0'))
        {
            throw ApiProblemException.Validation("bodyMd", "正文不能包含空字符。");
        }

        return body;
    }

    private static (string? Kernel, string? Platform, string? Model) NormalizeEnvironment(
        EnvironmentInput? environment,
        Category category,
        string field)
    {
        var kernel = NormalizeOptional(environment?.KernelVersion);
        var platform = NormalizeOptional(environment?.Platform);
        var model = NormalizeOptional(environment?.Model);

        if (kernel is not null
            && (kernel.Length > 64
                || !KernelPattern.IsMatch(kernel)
                || kernel.Any(char.IsControl)))
        {
            throw ApiProblemException.Validation($"{field}.kernelVersion", "内核版本格式或长度无效。");
        }

        if (platform is not null
            && (!AllowedPlatforms.Contains(platform) || platform.Length > 30))
        {
            throw ApiProblemException.Validation($"{field}.platform", "平台不在受控枚举中。");
        }

        if (model is not null
            && (RuneHelper.Count(model) > 100 || model.Any(char.IsControl)))
        {
            throw ApiProblemException.Validation($"{field}.model", "模型名称不能超过 100 个字符。");
        }

        if (category.Name == "抄作业" && string.IsNullOrWhiteSpace(kernel))
        {
            throw ApiProblemException.Validation($"{field}.kernelVersion", "“抄作业”板块必须填写内核版本。");
        }

        return (kernel, platform, model);
    }

    private static List<NormalizedArtifact> NormalizeArtifacts(
        IReadOnlyList<ArtifactInput>? artifacts,
        string field)
    {
        if (artifacts is null || artifacts.Count == 0)
        {
            return [];
        }

        if (artifacts.Count > 5)
        {
            throw ApiProblemException.Validation(field, "产物最多 5 份。");
        }

        var normalized = new List<NormalizedArtifact>(artifacts.Count);
        for (var index = 0; index < artifacts.Count; index++)
        {
            var artifact = artifacts[index];
            if (artifact is null)
            {
                throw ApiProblemException.Validation($"{field}[{index}]", "产物不能为空。");
            }

            var kind = (artifact.Kind ?? string.Empty).Trim().ToLowerInvariant();
            var name = (artifact.Name ?? string.Empty).Trim();
            var note = NormalizeOptional(artifact.Note);
            var content = artifact.Content ?? string.Empty;

            if (!AllowedArtifactKinds.Contains(kind))
            {
                throw ApiProblemException.Validation($"{field}[{index}].kind", "产物类型无效。");
            }

            if (RuneHelper.Count(name) is < 1 or > 120 || name.Any(character => char.IsControl(character)))
            {
                throw ApiProblemException.Validation($"{field}[{index}].name", "产物名称必须为 1–120 个字符。");
            }

            if (note is not null
                && (RuneHelper.Count(note) > 200 || note.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t')))
            {
                throw ApiProblemException.Validation($"{field}[{index}].note", "产物备注不能超过 200 个字符。");
            }

            if (RuneHelper.Count(content) is < 1 or > 200_000 || content.Contains('\0'))
            {
                throw ApiProblemException.Validation($"{field}[{index}].content", "产物内容必须为 1–200,000 个字符的纯文本。");
            }

            normalized.Add(new NormalizedArtifact(kind, name, note, content));
        }

        return normalized;
    }

    private async Task<List<LoadedUpload>> LoadUploadsAsync(
        Guid actorId,
        IReadOnlyList<PostImageInput>? images,
        string field,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (images is null || images.Count == 0)
        {
            return [];
        }

        if (images.Count > 10)
        {
            throw ApiProblemException.Validation(field, "图片最多 10 张。");
        }

        var ids = new List<Guid>(images.Count);
        foreach (var image in images)
        {
            if (image is null)
            {
                throw ApiProblemException.Validation(field, "图片不能为空。");
            }

            if (image.Id == Guid.Empty)
            {
                throw ApiProblemException.Validation(field, "图片上传 ID 无效。");
            }

            if (ids.Contains(image.Id))
            {
                throw ApiProblemException.Validation(field, "图片上传 ID 不能重复。");
            }

            ids.Add(image.Id);
        }

        var uploads = await db.MediaUploads
            .Where(upload => ids.Contains(upload.Id))
            .ToListAsync(cancellationToken);
        if (uploads.Count != ids.Count)
        {
            throw ApiProblemException.Validation(field, "图片上传记录不存在。");
        }

        var alreadyAttached = await db.PostImages
            .AsNoTracking()
            .Where(image => ids.Contains(image.UploadId))
            .Select(image => image.UploadId)
            .ToListAsync(cancellationToken);

        var result = new List<LoadedUpload>(images.Count);
        foreach (var image in images)
        {
            var upload = uploads.Single(candidate => candidate.Id == image.Id); if (upload.OwnerId != actorId
                || upload.Status != MediaUploadStatuses.Temp
                || upload.ExpiresAt <= now
                || alreadyAttached.Contains(upload.Id))
            {
                throw ApiProblemException.Validation(field, "图片上传不存在、已使用或不属于当前用户。");
            }

            result.Add(new LoadedUpload(
                upload,
                NormalizeAltText(image.AltText, field)));
        }

        return result;
    }

    private async Task ReplaceImagesAsync(
        Guid actorId,
        Post post,
        IReadOnlyList<Guid>? keepImageIds,
        IReadOnlyList<PostImageInput>? images,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existingImages = await db.PostImages
            .Where(image => image.PostId == post.Id)
            .OrderBy(image => image.SortOrder)
            .ThenBy(image => image.Id)
            .ToListAsync(cancellationToken);
        var keepIds = keepImageIds?.ToList() ?? [];
        if (keepIds.Count > 10 || keepIds.Distinct().Count() != keepIds.Count)
        {
            throw ApiProblemException.Validation("keepImageIds", "保留图片最多 10 张且不能重复。");
        }

        var existingById = existingImages.ToDictionary(image => image.Id);
        foreach (var imageId in keepIds)
        {
            if (!existingById.ContainsKey(imageId))
            {
                throw ApiProblemException.Validation("keepImageIds", "只能保留本帖已有的图片。");
            }
        }

        var newUploads = await LoadUploadsAsync(actorId, images, "images", now, cancellationToken);
        if (keepIds.Count + newUploads.Count > 10)
        {
            throw ApiProblemException.Validation("images", "图片合计最多 10 张。");
        }

        var keepSet = keepIds.ToHashSet();
        foreach (var image in existingImages.Where(image => !keepSet.Contains(image.Id)))
        {
            var upload = await db.MediaUploads
                .SingleOrDefaultAsync(candidate => candidate.Id == image.UploadId, cancellationToken);
            if (upload is not null)
            {
                upload.Status = MediaUploadStatuses.Deleted;
                upload.PostId = null;
            }

            db.PostImages.Remove(image);
        }

        var sortOrder = 0;
        foreach (var imageId in keepIds)
        {
            existingById[imageId].SortOrder = (short)sortOrder++;
        }

        foreach (var upload in newUploads)
        {
            AddImage(post.Id, upload, (short)sortOrder++, now);
        }
    }

    private void AddArtifacts(Guid postId, IReadOnlyList<NormalizedArtifact> artifacts, DateTimeOffset now)
    {
        for (var index = 0; index < artifacts.Count; index++)
        {
            var artifact = artifacts[index];
            db.Artifacts.Add(new Artifact
            {
                PostId = postId,
                Kind = artifact.Kind,
                Name = artifact.Name,
                Note = artifact.Note,
                Content = artifact.Content,
                SortOrder = (short)index,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
    }

    private void AddImages(Guid postId, IReadOnlyList<LoadedUpload> images, DateTimeOffset now)
    {
        for (var index = 0; index < images.Count; index++)
        {
            AddImage(postId, images[index], (short)index, now);
        }
    }

    private void AddImage(Guid postId, LoadedUpload image, short sortOrder, DateTimeOffset now)
    {
        image.Upload.Status = MediaUploadStatuses.Attached;
        image.Upload.PostId = postId;
        image.Upload.AttachedAt = now;
        db.PostImages.Add(new PostImage
        {
            PostId = postId,
            UploadId = image.Upload.Id,
            Url = image.Upload.Url,
            Width = image.Upload.Width,
            Height = image.Upload.Height,
            AltText = image.AltText,
            MimeType = image.Upload.MimeType,
            ByteSize = image.Upload.ByteSize,
            SortOrder = sortOrder,
            CreatedAt = now
        });
    }

    private static string? NormalizeAltText(string? altText, string field)
    {
        var normalized = NormalizeOptional(altText);
        if (normalized is not null
            && (RuneHelper.Count(normalized) > 200
                || normalized.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t')))
        {
            throw ApiProblemException.Validation(field, "图片替代文本不能超过 200 个字符。");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static bool HasPostChanges(PostUpdateCommand command)
    {
        return command.Title is not null
            || command.CategoryId is not null
            || command.BodyMd is not null
            || command.Tags is not null
            || command.Environment is not null
            || command.Artifacts is not null
            || command.KeepImageIds is not null
            || command.Images is not null;
    }

    private static ApiProblemException VersionConflict(int expectedVersion, int? currentVersion)
    {
        return ApiProblemException.Conflict(
            "内容已被其他操作修改，请刷新后重试。",
            new { currentVersion, expectedVersion });
    }

    private sealed record NormalizedArtifact(string Kind, string Name, string? Note, string Content);

    private sealed record LoadedUpload(MediaUpload Upload, string? AltText);
}