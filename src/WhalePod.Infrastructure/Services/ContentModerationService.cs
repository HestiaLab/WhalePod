using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Domain.Moderation;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record AiSuggestionSummaryView(
    Guid Id,
    string Suggestion,
    string PolicyCode,
    decimal Confidence,
    string Reason,
    string ModelName,
    string ModelVersion,
    string ModelQuantization,
    DateTimeOffset CreatedAt);

public sealed record ModerationQueueItemView(
    string TargetType,
    Guid TargetId,
    int ContentVersion,
    string Title,
    string Excerpt,
    CategoryView Category,
    AuthorView Author,
    IReadOnlyList<string> RiskHints,
    int ReportCount,
    DateTimeOffset SubmittedAt,
    AiSuggestionSummaryView? AiSuggestion);

public sealed record ModerationQueuePage(
    IReadOnlyList<ModerationQueueItemView> Items,
    string? NextCursor,
    bool InvalidCursor);

public sealed record ModerationActionCommand(
    string? Action,
    string? Reason,
    int? ExpectedContentVersion,
    Guid? SuggestionId,
    string? OverrideReason);

public sealed record ModerationActionResultView(
    Guid ActionId,
    string TargetType,
    Guid TargetId,
    string ModerationStatus,
    int Version,
    string? TaskStatus,
    bool? SuggestionAdopted);

public sealed record ModerationPreviewView(
    string TargetType,
    Guid TargetId,
    int ContentVersion,
    string ModerationStatus,
    string Title,
    string BodyMd,
    string BodyHtml,
    CategoryView Category,
    AuthorView Author,
    Guid? PostId,
    string? PostTitle,
    IReadOnlyList<PostImageView> Images,
    IReadOnlyList<ArtifactView> Artifacts,
    IReadOnlyList<string> RiskHints,
    int ReportCount,
    DateTimeOffset SubmittedAt,
    AiSuggestionSummaryView? AiSuggestion);

public sealed class ContentModerationService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    EmailNotificationQueue emailQueue,
    TimeProvider timeProvider)
{
    private static readonly string[] ActiveTaskStatuses =
    [
        ModerationTaskStatuses.Queued,
        ModerationTaskStatuses.Claimed,
        ModerationTaskStatuses.Suggested,
        ModerationTaskStatuses.Escalated
    ];

    public async Task<ModerationQueuePage> GetQueueAsync(
        Guid actorId,
        string? targetType,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        var normalizedType = string.IsNullOrWhiteSpace(targetType) ? null : targetType.Trim().ToLowerInvariant();
        if (normalizedType is not null && normalizedType is not ("post" or "comment"))
        {
            throw ApiProblemException.Validation("targetType", "targetType 必须是 post 或 comment。");
        }

        var scope = AdminCursor.BuildScope("moderation-queue", normalizedType);
        DateTimeOffset? cursorTime = null;
        Guid cursorId = default;
        var cursorPrioritized = false;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!AdminCursor.TryDecode(
                    cursor,
                    scope,
                    out var decodedTime,
                    out var decodedId,
                    out var decodedPrioritized))
            {
                return new ModerationQueuePage([], null, true);
            }

            cursorTime = decodedTime;
            cursorId = decodedId;
            cursorPrioritized = decodedPrioritized;
        }

        var rows = new List<QueueSourceRow>();

        if (normalizedType is null or "post")
        {
            var query = db.Posts
                .AsNoTracking()
                .Where(post => post.ModerationStatus == ModerationStatuses.Pending && post.DeletedAt == null)
                .Select(post => new
                {
                    post.Id,
                    post.Title,
                    post.Excerpt,
                    post.Version,
                    post.UpdatedAt,
                    IsEscalated = db.ModerationActions.Any(action =>
                            action.TargetType == ModerationTargetTypes.Post
                            && action.TargetId == post.Id
                            && action.Action == ModerationActions.Escalate
                            && action.ContentVersion == post.Version)
                        || db.ModerationTasks.Any(task =>
                            task.TargetType == ModerationTargetTypes.Post
                            && task.TargetId == post.Id
                            && task.Status == ModerationTaskStatuses.Escalated
                            && task.ContentVersion == post.Version),
                    Category = db.Categories.Single(category => category.Id == post.CategoryId),
                    Author = db.Users.Single(user => user.Id == post.AuthorId),
                    ActiveDeviceCount = db.Devices.Count(device =>
                        device.UserId == post.AuthorId && device.Status == DeviceStatuses.Active),
                    LastVerifiedAt = db.Devices
                        .Where(device => device.UserId == post.AuthorId && device.Status == DeviceStatuses.Active)
                        .Max(device => (DateTimeOffset?)device.LastVerifiedAt),
                    ReportCount = db.Reports.Count(report =>
                        report.TargetType == ReportTargetTypes.Post
                        && report.TargetId == post.Id
                        && report.Status == ReportStatuses.Pending)
                });
            if (cursorTime is not null)
            {
                var afterCursor = cursorId;
                var cursorAt = cursorTime.Value;
                query = cursorPrioritized
                    ? query.Where(row => !row.IsEscalated
                        || (row.IsEscalated
                            && (row.UpdatedAt > cursorAt
                                || (row.UpdatedAt == cursorAt && row.Id.CompareTo(afterCursor) > 0))))
                    : query.Where(row => !row.IsEscalated
                        && (row.UpdatedAt > cursorAt
                            || (row.UpdatedAt == cursorAt && row.Id.CompareTo(afterCursor) > 0)));
            }

            var postRows = await query
                .OrderByDescending(row => row.IsEscalated)
                .ThenBy(row => row.UpdatedAt)
                .ThenBy(row => row.Id)
                .Take(limit + 1)
                .ToListAsync(cancellationToken);

            rows.AddRange(postRows.Select(row => new QueueSourceRow(
                "post",
                row.Id,
                row.Version,
                row.Title,
                row.Excerpt,
                row.Category,
                row.Author,
                row.ActiveDeviceCount,
                row.LastVerifiedAt,
                row.ReportCount,
                row.UpdatedAt,
                row.IsEscalated)));
        }

        if (normalizedType is null or "comment")
        {
            var query = db.Comments
                .AsNoTracking()
                .Where(comment => comment.ModerationStatus == ModerationStatuses.Pending
                    && comment.DeletedAt == null)
                .Select(comment => new
                {
                    comment.Id,
                    comment.PostId,
                    comment.BodyMd,
                    comment.Version,
                    comment.UpdatedAt,
                    IsEscalated = db.ModerationActions.Any(action =>
                            action.TargetType == ModerationTargetTypes.Comment
                            && action.TargetId == comment.Id
                            && action.Action == ModerationActions.Escalate
                            && action.ContentVersion == comment.Version)
                        || db.ModerationTasks.Any(task =>
                            task.TargetType == ModerationTargetTypes.Comment
                            && task.TargetId == comment.Id
                            && task.Status == ModerationTaskStatuses.Escalated
                            && task.ContentVersion == comment.Version),
                    Category = db.Categories.Single(category =>
                        category.Id == db.Posts.Single(post => post.Id == comment.PostId).CategoryId),
                    Author = db.Users.Single(user => user.Id == comment.AuthorId),
                    ActiveDeviceCount = db.Devices.Count(device =>
                        device.UserId == comment.AuthorId && device.Status == DeviceStatuses.Active),
                    LastVerifiedAt = db.Devices
                        .Where(device => device.UserId == comment.AuthorId && device.Status == DeviceStatuses.Active)
                        .Max(device => (DateTimeOffset?)device.LastVerifiedAt),
                    ReportCount = db.Reports.Count(report =>
                        report.TargetType == ReportTargetTypes.Comment
                        && report.TargetId == comment.Id
                        && report.Status == ReportStatuses.Pending)
                });
            if (cursorTime is not null)
            {
                var afterCursor = cursorId;
                var cursorAt = cursorTime.Value;
                query = cursorPrioritized
                    ? query.Where(row => !row.IsEscalated
                        || (row.IsEscalated
                            && (row.UpdatedAt > cursorAt
                                || (row.UpdatedAt == cursorAt && row.Id.CompareTo(afterCursor) > 0))))
                    : query.Where(row => !row.IsEscalated
                        && (row.UpdatedAt > cursorAt
                            || (row.UpdatedAt == cursorAt && row.Id.CompareTo(afterCursor) > 0)));
            }

            var commentRows = await query
                .OrderByDescending(row => row.IsEscalated)
                .ThenBy(row => row.UpdatedAt)
                .ThenBy(row => row.Id)
                .Take(limit + 1)
                .ToListAsync(cancellationToken);

            rows.AddRange(commentRows.Select(row => new QueueSourceRow(
                "comment",
                row.Id,
                row.Version,
                "评论",
                RuneHelper.Truncate(row.BodyMd, 500),
                row.Category,
                row.Author,
                row.ActiveDeviceCount,
                row.LastVerifiedAt,
                row.ReportCount,
                row.UpdatedAt,
                row.IsEscalated)));
        }

        var ordered = rows
            .OrderByDescending(row => row.IsEscalated)
            .ThenBy(row => row.SubmittedAt)
            .ThenBy(row => row.TargetId)
            .ToList();
        var hasMore = ordered.Count > limit;
        var pageRows = hasMore ? ordered.Take(limit).ToList() : ordered;
        var insights = await LoadModerationInsightsAsync(pageRows, cancellationToken);
        var items = pageRows
            .Select(row =>
            {
                var key = (row.TargetType, row.TargetId, row.ContentVersion);
                return new ModerationQueueItemView(
                    row.TargetType,
                    row.TargetId,
                    row.ContentVersion,
                    row.Title,
                    RuneHelper.Truncate(row.Excerpt, 500),
                    PublicContentService.ToCategory(row.Category),
                    PublicContentService.ToAuthor(row.Author, row.ActiveDeviceCount, row.LastVerifiedAt),
                    insights.RiskHints.GetValueOrDefault(key, []),
                    row.ReportCount,
                    row.SubmittedAt,
                    insights.Suggestions.GetValueOrDefault(key));
            })
            .ToList();

        var nextCursor = hasMore
            ? AdminCursor.EncodePrioritized(
                scope,
                pageRows[^1].SubmittedAt,
                pageRows[^1].TargetId,
                pageRows[^1].IsEscalated)
            : null;
        return new ModerationQueuePage(items, nextCursor, false);
    }

    private async Task<(Dictionary<(string, Guid, int), AiSuggestionSummaryView> Suggestions,
        Dictionary<(string, Guid, int), IReadOnlyList<string>> RiskHints)> LoadModerationInsightsAsync(
        IReadOnlyList<QueueSourceRow> rows,
        CancellationToken cancellationToken)
    {
        var suggestions = new Dictionary<(string, Guid, int), AiSuggestionSummaryView>();
        var riskHints = new Dictionary<(string, Guid, int), IReadOnlyList<string>>();
        if (rows.Count == 0)
        {
            return (suggestions, riskHints);
        }

        var currentVersions = rows.ToDictionary(
            row => (row.TargetType, row.TargetId),
            row => row.ContentVersion);
        var postIds = rows
            .Where(row => row.TargetType == ModerationTargetTypes.Post)
            .Select(row => row.TargetId)
            .Distinct()
            .ToList();
        var commentIds = rows
            .Where(row => row.TargetType == ModerationTargetTypes.Comment)
            .Select(row => row.TargetId)
            .Distinct()
            .ToList();

        var suggestionRows = await (
                from task in db.ModerationTasks.AsNoTracking()
                join suggestion in db.ModerationSuggestions.AsNoTracking() on task.Id equals suggestion.TaskId
                where (task.TargetType == ModerationTargetTypes.Post && postIds.Contains(task.TargetId))
                    || (task.TargetType == ModerationTargetTypes.Comment && commentIds.Contains(task.TargetId))
                select new { task.TargetType, task.TargetId, task.ContentVersion, Suggestion = suggestion })
            .ToListAsync(cancellationToken);

        foreach (var group in suggestionRows
                     .Where(row => currentVersions.TryGetValue((row.TargetType, row.TargetId), out var currentVersion)
                         && currentVersion == row.ContentVersion)
                     .GroupBy(row => (row.TargetType, row.TargetId, row.ContentVersion)))
        {
            var latest = group
                .OrderByDescending(row => row.Suggestion.CreatedAt)
                .ThenByDescending(row => row.Suggestion.Id)
                .First()
                .Suggestion;
            suggestions[group.Key] = new AiSuggestionSummaryView(
                latest.Id,
                latest.Suggestion,
                latest.PolicyCode,
                latest.Confidence,
                latest.Reason,
                latest.ModelName,
                latest.ModelVersion,
                latest.ModelQuantization,
                latest.CreatedAt);
        }

        var checkRows = await db.ModerationChecks
            .AsNoTracking()
            .Where(check => (check.TargetType == ModerationTargetTypes.Post && postIds.Contains(check.TargetId))
                || (check.TargetType == ModerationTargetTypes.Comment && commentIds.Contains(check.TargetId)))
            .Select(check => new
            {
                check.TargetType,
                check.TargetId,
                check.ContentVersion,
                check.RiskLabels,
                check.CreatedAt,
                check.Id
            })
            .ToListAsync(cancellationToken);

        foreach (var group in checkRows
                     .Where(row => currentVersions.TryGetValue((row.TargetType, row.TargetId), out var currentVersion)
                         && currentVersion == row.ContentVersion)
                     .GroupBy(row => (row.TargetType, row.TargetId, row.ContentVersion)))
        {
            var latest = group
                .OrderByDescending(row => row.CreatedAt)
                .ThenByDescending(row => row.Id)
                .First();
            riskHints[group.Key] = ReadRiskLabels(latest.RiskLabels);
        }

        return (suggestions, riskHints);
    }

    private static IReadOnlyList<string> ReadRiskLabels(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement
            .EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<ModerationPreviewView?> GetPreviewAsync(
        Guid actorId,
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        var normalizedType = NormalizeTargetType(targetType);
        QueueSourceRow source;
        string moderationStatus;
        string bodyMd;
        string bodyHtml;
        Guid? postId = null;
        string? postTitle = null;
        IReadOnlyList<PostImageView> images = [];
        IReadOnlyList<ArtifactView> artifacts = [];

        if (normalizedType == ModerationTargetTypes.Post)
        {
            var row = await db.Posts
                .AsNoTracking()
                .Where(post => post.Id == targetId && post.DeletedAt == null)
                .Select(post => new
                {
                    Post = post,
                    Category = db.Categories.Single(category => category.Id == post.CategoryId),
                    Author = db.Users.Single(user => user.Id == post.AuthorId),
                    ActiveDeviceCount = db.Devices.Count(device =>
                        device.UserId == post.AuthorId && device.Status == DeviceStatuses.Active),
                    LastVerifiedAt = db.Devices
                        .Where(device => device.UserId == post.AuthorId
                            && device.Status == DeviceStatuses.Active)
                        .Max(device => (DateTimeOffset?)device.LastVerifiedAt),
                    ReportCount = db.Reports.Count(report =>
                        report.TargetType == ReportTargetTypes.Post
                        && report.TargetId == post.Id
                        && report.Status == ReportStatuses.Pending)
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                return null;
            }

            bodyMd = row.Post.BodyMd;
            bodyHtml = row.Post.BodyHtml;
            moderationStatus = row.Post.ModerationStatus;
            images = await db.PostImages
                .AsNoTracking()
                .Where(image => image.PostId == row.Post.Id)
                .OrderBy(image => image.SortOrder)
                .ThenBy(image => image.CreatedAt)
                .Select(image => new PostImageView(
                    image.Id,
                    image.Url,
                    image.Width,
                    image.Height,
                    image.AltText,
                    image.MimeType,
                    image.SortOrder))
                .ToListAsync(cancellationToken);
            artifacts = await db.Artifacts
                .AsNoTracking()
                .Where(artifact => artifact.PostId == row.Post.Id)
                .OrderBy(artifact => artifact.SortOrder)
                .ThenBy(artifact => artifact.CreatedAt)
                .Select(artifact => new ArtifactView(
                    artifact.Id,
                    artifact.Kind,
                    artifact.Name,
                    artifact.Note,
                    artifact.Content,
                    artifact.CopyCount,
                    artifact.SortOrder))
                .ToListAsync(cancellationToken);
            source = new QueueSourceRow(
                ModerationTargetTypes.Post,
                row.Post.Id,
                row.Post.Version,
                row.Post.Title,
                row.Post.Excerpt,
                row.Category,
                row.Author,
                row.ActiveDeviceCount,
                row.LastVerifiedAt,
                row.ReportCount,
                row.Post.UpdatedAt,
                false);
        }
        else
        {
            var row = await db.Comments
                .AsNoTracking()
                .Where(comment => comment.Id == targetId && comment.DeletedAt == null)
                .Select(comment => new
                {
                    Comment = comment,
                    PostTitle = db.Posts
                        .Where(post => post.Id == comment.PostId)
                        .Select(post => post.Title)
                        .Single(),
                    Category = db.Categories.Single(category =>
                        category.Id == db.Posts.Single(post => post.Id == comment.PostId).CategoryId),
                    Author = db.Users.Single(user => user.Id == comment.AuthorId),
                    ActiveDeviceCount = db.Devices.Count(device =>
                        device.UserId == comment.AuthorId && device.Status == DeviceStatuses.Active),
                    LastVerifiedAt = db.Devices
                        .Where(device => device.UserId == comment.AuthorId
                            && device.Status == DeviceStatuses.Active)
                        .Max(device => (DateTimeOffset?)device.LastVerifiedAt),
                    ReportCount = db.Reports.Count(report =>
                        report.TargetType == ReportTargetTypes.Comment
                        && report.TargetId == comment.Id
                        && report.Status == ReportStatuses.Pending)
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                return null;
            }

            bodyMd = row.Comment.BodyMd;
            bodyHtml = row.Comment.BodyHtml;
            moderationStatus = row.Comment.ModerationStatus;
            postId = row.Comment.PostId;
            postTitle = row.PostTitle;
            source = new QueueSourceRow(
                ModerationTargetTypes.Comment,
                row.Comment.Id,
                row.Comment.Version,
                "评论",
                RuneHelper.Truncate(row.Comment.BodyMd, 500),
                row.Category,
                row.Author,
                row.ActiveDeviceCount,
                row.LastVerifiedAt,
                row.ReportCount,
                row.Comment.UpdatedAt,
                false);
        }

        var insights = await LoadModerationInsightsAsync([source], cancellationToken);
        var key = (source.TargetType, source.TargetId, source.ContentVersion);
        return new ModerationPreviewView(
            source.TargetType,
            source.TargetId,
            source.ContentVersion,
            moderationStatus,
            source.Title,
            bodyMd,
            bodyHtml,
            PublicContentService.ToCategory(source.Category),
            PublicContentService.ToAuthor(
                source.Author,
                source.ActiveDeviceCount,
                source.LastVerifiedAt),
            postId,
            postTitle,
            images,
            artifacts,
            insights.RiskHints.GetValueOrDefault(key, []),
            source.ReportCount,
            source.SubmittedAt,
            insights.Suggestions.GetValueOrDefault(key));
    }

    public async Task<ModerationActionResultView> ExecuteAsync(
        Guid actorId,
        string targetType,
        Guid targetId,
        ModerationActionCommand command,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        var normalizedType = NormalizeTargetType(targetType);
        var action = NormalizeAction(command.Action);
        if (command.ExpectedContentVersion is null or < 1)
        {
            throw ApiProblemException.Validation("expectedContentVersion", "expectedContentVersion 必须为正整数。");
        }

        var ownsTransaction = db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var result = await ApplyCoreAsync(
                actorId,
                normalizedType,
                targetId,
                action,
                command.Reason,
                command.ExpectedContentVersion.Value,
                command.SuggestionId,
                command.OverrideReason,
                audit,
                cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public async Task<ModerationActionResultView> ApplyForReportAsync(
        Guid actorId,
        string targetType,
        Guid targetId,
        string action,
        string reason,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        var normalizedType = NormalizeTargetType(targetType);
        var normalizedAction = NormalizeAction(action);
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var currentVersion = await LoadCurrentVersionAsync(normalizedType, targetId, cancellationToken);
            if (currentVersion is null)
            {
                throw ApiProblemException.NotFound("举报目标内容不存在或已删除。");
            }

            var result = await ApplyCoreAsync(
                actorId,
                normalizedType,
                targetId,
                normalizedAction,
                reason,
                currentVersion.Value,
                null,
                null,
                audit,
                cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public async Task<Guid?> GetContentAuthorIdAsync(
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var normalizedType = NormalizeTargetType(targetType);
        return normalizedType == ModerationTargetTypes.Post
            ? await db.Posts
                .AsNoTracking()
                .Where(post => post.Id == targetId)
                .Select(post => (Guid?)post.AuthorId)
                .SingleOrDefaultAsync(cancellationToken)
            : await db.Comments
                .AsNoTracking()
                .Where(comment => comment.Id == targetId)
                .Select(comment => (Guid?)comment.AuthorId)
                .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<ModerationActionResultView> ApplyCoreAsync(
        Guid actorId,
        string targetType,
        Guid targetId,
        string action,
        string? reason,
        int expectedVersion,
        Guid? suggestionId,
        string? overrideReason,
        AuditContext audit,
        CancellationToken cancellationToken)
    {
        var normalizedReason = NormalizeReason(reason, action);
        var normalizedOverride = string.IsNullOrWhiteSpace(overrideReason) ? null : overrideReason.Trim();
        if (normalizedOverride is not null && RuneHelper.Count(normalizedOverride) > 1000)
        {
            throw ApiProblemException.Validation("overrideReason", "覆盖 AI 建议理由不能超过 1000 个字符。");
        }

        Post? post = null;
        Comment? comment = null;
        string fromStatus;
        Guid authorId;
        if (targetType == ModerationTargetTypes.Post)
        {
            post = await db.Posts
                .SingleOrDefaultAsync(candidate => candidate.Id == targetId && candidate.DeletedAt == null, cancellationToken);
            if (post is null)
            {
                throw ApiProblemException.NotFound("待审核内容不存在或已删除。");
            }

            fromStatus = post.ModerationStatus;
            authorId = post.AuthorId;
        }
        else
        {
            comment = await db.Comments
                .SingleOrDefaultAsync(candidate => candidate.Id == targetId && candidate.DeletedAt == null, cancellationToken);
            if (comment is null)
            {
                throw ApiProblemException.NotFound("待审核内容不存在或已删除。");
            }

            fromStatus = comment.ModerationStatus;
            authorId = comment.AuthorId;
        }

        var currentVersion = post?.Version ?? comment!.Version;
        if (currentVersion != expectedVersion)
        {
            throw ContentVersionStale(expectedVersion, currentVersion);
        }

        var toStatus = GetTargetStatus(action, fromStatus);
        if (toStatus is null)
        {
            throw ApiProblemException.Conflict("当前内容状态不允许执行该审核动作。", new
            {
                action,
                currentStatus = fromStatus
            });
        }

        var suggestion = suggestionId is null
            ? null
            : await LoadSuggestionAsync(targetType, targetId, currentVersion, suggestionId.Value, cancellationToken);
        bool? suggestionAdopted = null;
        if (suggestion is not null)
        {
            suggestionAdopted = string.Equals(suggestion.Suggestion, action, StringComparison.Ordinal);
            if (suggestionAdopted == false && normalizedOverride is null)
            {
                throw ApiProblemException.Validation("overrideReason", "与 AI 建议不一致时必须填写覆盖理由。");
            }
        }

        var now = timeProvider.GetUtcNow();
        var newVersion = currentVersion + 1;
        if (post is not null)
        {
            post.ModerationStatus = toStatus;
            post.Version = newVersion;
            post.UpdatedAt = now;
            if (action == ModerationActions.Delete)
            {
                post.DeletedAt = now;
            }
        }
        else
        {
            comment!.ModerationStatus = toStatus;
            comment.Version = newVersion;
            comment.UpdatedAt = now;
            if (action == ModerationActions.Delete)
            {
                comment.DeletedAt = now;
            }
        }

        var acceptedCleared = false;
        if (comment is not null && toStatus == ModerationStatuses.Approved)
        {
            var activityPost = await db.Posts
                .SingleAsync(candidate => candidate.Id == comment.PostId, cancellationToken);
            activityPost.LastActivityAt = now;
        }

        if (comment is not null
            && action is ModerationActions.Hide or ModerationActions.Delete or ModerationActions.Reject
            && (comment.IsAccepted || await IsAcceptedCommentAsync(comment, cancellationToken)))
        {
            acceptedCleared = true;
            comment.IsAccepted = false;
            var parentPost = await db.Posts.SingleAsync(candidate => candidate.Id == comment.PostId, cancellationToken);
            parentPost.AcceptedCommentId = null;
            parentPost.IsSolved = false;
            parentPost.Version++;
            parentPost.UpdatedAt = now;
            db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                audit,
                "post.acceptance_cleared",
                "post",
                parentPost.Id,
                new { commentId = comment.Id, reason = "moderated_comment" },
                now));
            if (parentPost.AuthorId != comment.AuthorId)
            {
                var payload = new
                {
                    summary = "被采纳的评论已离开通过状态，帖子已恢复为未解决。",
                    targetType = "post",
                    targetId = parentPost.Id,
                    targetUrl = $"/posts/{parentPost.Id}",
                    action = "acceptance_cleared"
                };
                var dedupeKey = $"comment-acceptance-cleared:{comment.Id:N}:{newVersion}";
                db.Notifications.Add(NotificationWriter.Create(
                    parentPost.AuthorId,
                    NotificationTypes.CommentAccepted,
                    payload,
                    dedupeKey,
                    now));
                await emailQueue.EnqueueForNotificationAsync(
                    parentPost.AuthorId,
                    NotificationTypes.CommentAccepted,
                    payload,
                    dedupeKey,
                    now,
                    cancellationToken);
            }
        }

        var moderationAction = new ModerationAction
        {
            TargetType = targetType,
            TargetId = targetId,
            ActorType = ModerationActorTypes.User,
            ActorId = actorId,
            Action = action,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            Reason = normalizedReason,
            ContentVersion = newVersion,
            CreatedAt = now
        };
        db.ModerationActions.Add(moderationAction);

        if (suggestion is not null)
        {
            suggestion.HumanDecision = action;
            suggestion.HumanDecisionBy = actorId;
            suggestion.HumanDecisionReason = normalizedOverride ?? normalizedReason;
            suggestion.HumanDecisionAt = now;
            suggestion.ModerationActionId = moderationAction.Id;
        }

        var tasks = await db.ModerationTasks
            .Where(task => task.TargetType == targetType
                && task.TargetId == targetId
                && ActiveTaskStatuses.Contains(task.Status))
            .ToListAsync(cancellationToken);
        foreach (var task in tasks)
        {
            task.LeaseExpiresAt = null;
            task.LeaseToken = null;
            if (action == ModerationActions.Escalate)
            {
                task.Status = ModerationTaskStatuses.Escalated;
            }
            else
            {
                task.Status = ModerationTaskStatuses.HumanDecided;
                task.HumanDecidedAt = now;
                task.CompletedAt = now;
            }
        }

        if (action != ModerationActions.Escalate)
        {
            var notificationType = action is ModerationActions.Approve or ModerationActions.Restore
                ? targetType == ModerationTargetTypes.Post
                    ? NotificationTypes.PostApproved
                    : NotificationTypes.CommentApproved
                : targetType == ModerationTargetTypes.Post
                    ? NotificationTypes.PostRejected
                    : NotificationTypes.CommentRejected;
            var notificationPayload = new
            {
                targetType,
                targetId,
                postId = comment?.PostId,
                targetUrl = targetType == ModerationTargetTypes.Comment && comment is not null
                    ? $"/posts/{comment.PostId}?comment={targetId}"
                    : targetType == ModerationTargetTypes.Post
                        ? $"/posts/{targetId}"
                        : null,
                action,
                moderationStatus = toStatus,
                reason = normalizedReason
            };
            var dedupeKey = $"moderation:{targetType}:{targetId}:{newVersion}";
            db.Notifications.Add(NotificationWriter.Create(
                authorId,
                notificationType,
                notificationPayload,
                dedupeKey,
                now));
            await emailQueue.EnqueueForNotificationAsync(
                authorId,
                notificationType,
                notificationPayload,
                dedupeKey,
                now,
                cancellationToken);
        }

        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            $"moderation.{action}",
            targetType,
            targetId,
            new
            {
                fromStatus,
                toStatus,
                version = newVersion,
                reason = normalizedReason,
                overrideReason = normalizedOverride,
                suggestionId,
                suggestionAdopted,
                acceptedCleared
            },
            now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ContentVersionStale(expectedVersion, currentVersion);
        }

        var taskStatus = tasks
            .OrderByDescending(task => task.CreatedAt)
            .Select(task => task.Status)
            .FirstOrDefault();

        return new ModerationActionResultView(
            moderationAction.Id,
            targetType,
            targetId,
            toStatus,
            newVersion,
            taskStatus,
            suggestionAdopted);
    }

    private async Task<ModerationSuggestion?> LoadSuggestionAsync(
        string targetType,
        Guid targetId,
        int contentVersion,
        Guid suggestionId,
        CancellationToken cancellationToken)
    {
        var suggestion = await db.ModerationSuggestions
            .Where(candidate => candidate.Id == suggestionId)
            .Join(
                db.ModerationTasks,
                suggestion => suggestion.TaskId,
                task => task.Id,
                (suggestion, task) => new { Suggestion = suggestion, Task = task })
            .Select(row => row.Suggestion)
            .SingleOrDefaultAsync(cancellationToken);
        if (suggestion is null)
        {
            throw ApiProblemException.Validation("suggestionId", "AI 建议不存在。");
        }

        var task = await db.ModerationTasks
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == suggestion.TaskId, cancellationToken);
        if (task.TargetType != targetType
            || task.TargetId != targetId
            || task.ContentVersion != contentVersion)
        {
            throw ApiProblemException.Validation("suggestionId", "AI 建议与当前内容版本不匹配。");
        }

        if (suggestion.HumanDecision is not null)
        {
            throw ApiProblemException.Conflict("该 AI 建议已被裁决。");
        }

        return suggestion;
    }

    private async Task<bool> IsAcceptedCommentAsync(Comment comment, CancellationToken cancellationToken)
    {
        return await db.Posts
            .AsNoTracking()
            .AnyAsync(post => post.Id == comment.PostId && post.AcceptedCommentId == comment.Id, cancellationToken);
    }

    private async Task<int?> LoadCurrentVersionAsync(
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        return targetType == ModerationTargetTypes.Post
            ? await db.Posts
                .AsNoTracking()
                .Where(post => post.Id == targetId && post.DeletedAt == null)
                .Select(post => (int?)post.Version)
                .SingleOrDefaultAsync(cancellationToken)
            : await db.Comments
                .AsNoTracking()
                .Where(comment => comment.Id == targetId && comment.DeletedAt == null)
                .Select(comment => (int?)comment.Version)
                .SingleOrDefaultAsync(cancellationToken);
    }

    private static string NormalizeTargetType(string? targetType)
    {
        var normalized = (targetType ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not (ModerationTargetTypes.Post or ModerationTargetTypes.Comment))
        {
            throw ApiProblemException.Validation("targetType", "targetType 必须是 post 或 comment。");
        }

        return normalized;
    }

    private static string NormalizeAction(string? action)
    {
        var normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not (ModerationActions.Approve
            or ModerationActions.Reject
            or ModerationActions.Hide
            or ModerationActions.Restore
            or ModerationActions.Delete
            or ModerationActions.Escalate))
        {
            throw ApiProblemException.Validation("action", "审核动作无效。");
        }

        return normalized;
    }

    private static string? NormalizeReason(string? reason, string action)
    {
        var normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (action is ModerationActions.Reject or ModerationActions.Hide or ModerationActions.Delete or ModerationActions.Escalate
            && normalized is null)
        {
            throw ApiProblemException.Validation("reason", "该审核动作必须填写原因。");
        }

        if (normalized is not null && RuneHelper.Count(normalized) > 500)
        {
            throw ApiProblemException.Validation("reason", "原因不能超过 500 个字符。");
        }

        return normalized;
    }

    private static string? GetTargetStatus(string action, string fromStatus)
    {
        return action switch
        {
            ModerationActions.Approve when fromStatus == ModerationStatuses.Pending => ModerationStatuses.Approved,
            ModerationActions.Reject when fromStatus == ModerationStatuses.Pending => ModerationStatuses.Rejected,
            ModerationActions.Hide when fromStatus == ModerationStatuses.Approved => ModerationStatuses.Hidden,
            ModerationActions.Restore when fromStatus == ModerationStatuses.Hidden => ModerationStatuses.Approved,
            ModerationActions.Delete when fromStatus is ModerationStatuses.Pending
                or ModerationStatuses.Approved
                or ModerationStatuses.Rejected
                or ModerationStatuses.Hidden => ModerationStatuses.Deleted,
            ModerationActions.Escalate when fromStatus == ModerationStatuses.Pending => ModerationStatuses.Pending,
            _ => null
        };
    }

    private static ApiProblemException ContentVersionStale(int expectedVersion, int currentVersion)
    {
        return new ApiProblemException(
            ApiErrorCodes.ContentVersionStale,
            409,
            "内容版本已变化，请刷新后重试。",
            new { currentVersion, expectedVersion });
    }

    private sealed record QueueSourceRow(
        string TargetType,
        Guid TargetId,
        int ContentVersion,
        string Title,
        string Excerpt,
        Category Category,
        User Author,
        int ActiveDeviceCount,
        DateTimeOffset? LastVerifiedAt,
        int ReportCount,
        DateTimeOffset SubmittedAt,
        bool IsEscalated);
}