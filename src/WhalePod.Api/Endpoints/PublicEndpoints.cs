using System.Security.Claims;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Endpoints;

internal static class PublicEndpoints
{
    public static void MapPublicEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/categories", async (
            PublicContentService content,
            CancellationToken cancellationToken) =>
        {
            var categories = await content.GetCategoriesAsync(cancellationToken);
            return Results.Ok(new { data = new { items = categories } });
        });

        app.MapGet("/api/v1/tags", async (
            string? limit,
            string? cursor,
            PublicContentService content,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseLimit(limit, out var parsedLimit, out var limitError))
            {
                return limitError!;
            }

            if (cursor?.Length > 512)
            {
                return ApiError("VALIDATION_FAILED", "cursor 过长。", StatusCodes.Status400BadRequest);
            }

            var page = await content.GetTagsAsync(parsedLimit, cursor, cancellationToken);
            if (page.InvalidCursor)
            {
                return ApiError("VALIDATION_FAILED", "cursor 无效。", StatusCodes.Status400BadRequest);
            }

            return Results.Ok(new
            {
                data = new
                {
                    items = page.Items,
                    nextCursor = page.NextCursor
                }
            });
        });

        app.MapGet("/api/v1/feed", async (
            string? sort,
            string? category,
            string? tag,
            string? limit,
            string? cursor,
            PublicContentService content,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseLimit(limit, out var parsedLimit, out var limitError))
            {
                return limitError!;
            }

            var normalizedSort = string.IsNullOrWhiteSpace(sort) ? "latest" : sort.Trim().ToLowerInvariant();
            if (normalizedSort is not ("latest" or "active" or "unresolved"))
            {
                return ApiError("VALIDATION_FAILED", "sort 必须是 latest、active 或 unresolved。", StatusCodes.Status400BadRequest);
            }

            if (cursor?.Length > 512)
            {
                return ApiError("VALIDATION_FAILED", "cursor 过长。", StatusCodes.Status400BadRequest);
            }

            if (category?.Trim().Length > 40 || tag?.Trim().Length > 40)
            {
                return ApiError("VALIDATION_FAILED", "板块或标签 slug 过长。", StatusCodes.Status400BadRequest);
            }

            var page = await content.GetFeedAsync(
                normalizedSort,
                category,
                tag,
                parsedLimit,
                cursor,
                cancellationToken);

            if (page.InvalidCursor)
            {
                return ApiError("VALIDATION_FAILED", "cursor 无效或与当前排序不匹配。", StatusCodes.Status400BadRequest);
            }

            return Results.Ok(new
            {
                data = new
                {
                    items = page.Items,
                    nextCursor = page.NextCursor
                }
            });
        });

        app.MapGet("/api/v1/posts/{postId}", async (
            string postId,
            HttpContext httpContext,
            PublicContentService content,
            AdminAccessService adminAccess,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(postId, out var parsedPostId))
            {
                return ApiError("NOT_FOUND", "帖子不存在或不可见。", StatusCodes.Status404NotFound);
            }

            Guid? viewerId = Guid.TryParse(
                httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier),
                out var parsedUserId)
                ? parsedUserId
                : null;
            var viewerIsAdmin = viewerId is not null
                && await adminAccess.IsAdminAsync(viewerId.Value, cancellationToken);
            var post = await content.GetPostForViewerAsync(
                parsedPostId,
                viewerId,
                viewerIsAdmin,
                cancellationToken);
            if (post is null)
            {
                return ApiError("NOT_FOUND", "帖子不存在或不可见。", StatusCodes.Status404NotFound);
            }

            httpContext.Response.Headers.ETag = $"\"v{post.Version}\"";
            return Results.Ok(new { data = post });
        });

        app.MapGet("/api/v1/posts/{postId}/comments", async (
            string postId,
            string? sort,
            string? limit,
            string? cursor,
            PublicContentService content,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(postId, out var parsedPostId))
            {
                return ApiError("NOT_FOUND", "帖子不存在或不可见。", StatusCodes.Status404NotFound);
            }

            if (!TryParseLimit(limit, out var parsedLimit, out var limitError))
            {
                return limitError!;
            }

            var normalizedSort = string.IsNullOrWhiteSpace(sort) ? "latest" : sort.Trim().ToLowerInvariant();
            if (normalizedSort is not ("latest" or "top"))
            {
                return ApiError("VALIDATION_FAILED", "sort 必须是 latest 或 top。", StatusCodes.Status400BadRequest);
            }

            if (cursor?.Length > 512)
            {
                return ApiError("VALIDATION_FAILED", "cursor 过长。", StatusCodes.Status400BadRequest);
            }

            var page = await content.GetCommentsAsync(
                parsedPostId,
                normalizedSort,
                parsedLimit,
                cursor,
                cancellationToken);

            if (page is null)
            {
                return ApiError("NOT_FOUND", "帖子不存在或不可见。", StatusCodes.Status404NotFound);
            }

            if (page.InvalidCursor)
            {
                return ApiError("VALIDATION_FAILED", "cursor 无效或与当前排序不匹配。", StatusCodes.Status400BadRequest);
            }

            return Results.Ok(new
            {
                data = new
                {
                    items = page.Items,
                    nextCursor = page.NextCursor
                }
            });
        });

        app.MapGet("/api/v1/tags/{slug}", async (
            string slug,
            string? limit,
            string? cursor,
            PublicContentService content,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseLimit(limit, out var parsedLimit, out var limitError))
            {
                return limitError!;
            }

            if (cursor?.Length > 512)
            {
                return ApiError("VALIDATION_FAILED", "cursor 过长。", StatusCodes.Status400BadRequest);
            }

            var detail = await content.GetTagDetailAsync(slug, parsedLimit, cursor, cancellationToken);
            if (detail is null)
            {
                return ApiError("NOT_FOUND", "标签不存在或不可见。", StatusCodes.Status404NotFound);
            }

            if (detail.Posts.InvalidCursor)
            {
                return ApiError("VALIDATION_FAILED", "cursor 无效或与当前排序不匹配。", StatusCodes.Status400BadRequest);
            }

            return Results.Ok(new
            {
                data = new
                {
                    tag = detail.Tag,
                    posts = new
                    {
                        items = detail.Posts.Items,
                        nextCursor = detail.Posts.NextCursor
                    },
                    note = detail.Note
                }
            });
        });

        app.MapGet("/api/v1/users/{handle}", async (
            string handle,
            PublicContentService content,
            CancellationToken cancellationToken) =>
        {
            var profile = await content.GetUserProfileAsync(handle, cancellationToken);
            return profile is null
                ? ApiError("NOT_FOUND", "用户不存在或不可见。", StatusCodes.Status404NotFound)
                : Results.Ok(new { data = profile });
        });

        app.MapGet("/api/v1/users/{handle}/posts", async (
            string handle,
            string? limit,
            string? cursor,
            PublicContentService content,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseLimit(limit, out var parsedLimit, out var limitError))
            {
                return limitError!;
            }

            if (cursor?.Length > 512)
            {
                return ApiError("VALIDATION_FAILED", "cursor 过长。", StatusCodes.Status400BadRequest);
            }

            var page = await content.GetUserPostsAsync(handle, parsedLimit, cursor, cancellationToken);
            if (page is null)
            {
                return ApiError("NOT_FOUND", "用户不存在或不可见。", StatusCodes.Status404NotFound);
            }

            if (page.InvalidCursor)
            {
                return ApiError("VALIDATION_FAILED", "cursor 无效。", StatusCodes.Status400BadRequest);
            }

            return Results.Ok(new
            {
                data = new
                {
                    items = page.Items,
                    nextCursor = page.NextCursor
                }
            });
        });

        app.MapGet("/api/v1/search", async (
            string? q,
            string? type,
            string? limit,
            string? cursor,
            PublicSearchService search,
            CancellationToken cancellationToken) =>
        {
            var normalizedQuery = (q ?? string.Empty).Trim();
            if (normalizedQuery.Length == 0)
            {
                return ApiError("VALIDATION_FAILED", "关键词不能为空。", StatusCodes.Status400BadRequest);
            }

            if (normalizedQuery.Length > 100)
            {
                return ApiError("VALIDATION_FAILED", "关键词不能超过 100 个字符。", StatusCodes.Status400BadRequest);
            }

            var normalizedType = string.IsNullOrWhiteSpace(type) ? "posts" : type.Trim().ToLowerInvariant();
            if (normalizedType is not ("posts" or "users" or "tags"))
            {
                return ApiError("VALIDATION_FAILED", "type 必须是 posts、users 或 tags。", StatusCodes.Status400BadRequest);
            }

            if (!TryParseLimit(limit, out var parsedLimit, out var limitError))
            {
                return limitError!;
            }

            if (cursor?.Length > 512)
            {
                return ApiError("VALIDATION_FAILED", "cursor 过长。", StatusCodes.Status400BadRequest);
            }

            WhalePod.Infrastructure.Services.SearchResultView result;
            try
            {
                result = await search.SearchAsync(
                    normalizedQuery,
                    normalizedType,
                    parsedLimit,
                    cursor,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ApiError(
                    "SEARCH_TIMEOUT",
                    "搜索超时，请稍后重试。",
                    StatusCodes.Status504GatewayTimeout);
            }

            if (result.InvalidCursor)
            {
                return ApiError("VALIDATION_FAILED", "cursor 无效或与当前搜索不匹配。", StatusCodes.Status400BadRequest);
            }

            var data = new Dictionary<string, object?>
            {
                ["query"] = result.Query,
                ["type"] = result.Type,
                ["counts"] = result.Counts,
                ["nextCursor"] = result.NextCursor
            };

            if (result.Type == "posts")
            {
                data["posts"] = result.Posts;
            }
            else if (result.Type == "users")
            {
                data["users"] = result.Users;
            }
            else
            {
                data["tags"] = result.Tags;
            }

            return Results.Ok(new { data });
        }).RequireRateLimiting("public-search");
    }

    static bool TryParseLimit(string? raw, out int limit, out IResult? error)
    {
        limit = 20;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, out limit) || limit is < 1 or > 50)
        {
            error = ApiError(
                "VALIDATION_FAILED",
                "limit 必须是 1 到 50 之间的整数。",
                StatusCodes.Status400BadRequest);
            return false;
        }

        return true;
    }

    static IResult ApiError(string code, string message, int statusCode)
    {
        return Results.Json(new { code, message }, statusCode: statusCode);
    }
}