using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

using WhalePod.Api.Endpoints;
using WhalePod.Api.Services;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;
using WhalePod.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

var forwardedHeadersEnabled = builder.Configuration.GetValue("ReverseProxy:ForwardedHeadersEnabled", false);
if (forwardedHeadersEnabled)
{
    ConfigureForwardedHeaders(builder);
}

var maxJsonBodyBytes = builder.Configuration.GetValue("ApiRequestLimits:MaxJsonBodyBytes", 8 * 1024 * 1024);
if (maxJsonBodyBytes < 1)
{
    throw new InvalidOperationException("ApiRequestLimits:MaxJsonBodyBytes must be positive.");
}

var maxImageUploadRequestBytes = builder.Configuration.GetValue(
    "ApiRequestLimits:MaxImageUploadRequestBytes",
    (int)(MediaLimits.MaxImageBytes + 1024 * 1024));
if (maxImageUploadRequestBytes < 1)
{
    throw new InvalidOperationException("ApiRequestLimits:MaxImageUploadRequestBytes must be positive.");
}

var writeLimitPermit = builder.Configuration.GetValue("ApiWriteRateLimit:PermitLimit", 60);
var writeLimitWindowSeconds = builder.Configuration.GetValue("ApiWriteRateLimit:WindowSeconds", 60);
if (writeLimitPermit < 1 || writeLimitWindowSeconds < 1)
{
    throw new InvalidOperationException("ApiWriteRateLimit values must be positive.");
}

var uploadLimitPermit = builder.Configuration.GetValue("ApiUploadRateLimit:PermitLimit", 20);
var uploadLimitWindowSeconds = builder.Configuration.GetValue("ApiUploadRateLimit:WindowSeconds", 60);
if (uploadLimitPermit < 1 || uploadLimitWindowSeconds < 1)
{
    throw new InvalidOperationException("ApiUploadRateLimit values must be positive.");
}

var deviceChallengeLimit = builder.Configuration.GetValue(
    "DeviceVerification:ChallengeRateLimitPerMinute",
    10);
var deviceCompleteLimit = builder.Configuration.GetValue(
    "DeviceVerification:CompleteRateLimitPerMinute",
    10);
var deviceWriteLimit = builder.Configuration.GetValue(
    "DeviceVerification:WriteRateLimitPerMinute",
    30);
if (deviceChallengeLimit < 1 || deviceCompleteLimit < 1 || deviceWriteLimit < 1)
{
    throw new InvalidOperationException("DeviceVerification rate limits must be positive.");
}

var agentReadLimit = builder.Configuration.GetValue("AgentRateLimit:ReadPerMinute", 120);
var agentWriteLimit = builder.Configuration.GetValue("AgentRateLimit:WritePerMinute", 60);
var agentHeartbeatLimit = builder.Configuration.GetValue("AgentRateLimit:HeartbeatPerMinute", 12);
if (agentReadLimit < 1 || agentWriteLimit < 1 || agentHeartbeatLimit < 1)
{
    throw new InvalidOperationException("AgentRateLimit values must be positive.");
}

var connectionString = builder.Configuration.GetConnectionString("WhalePod")
    ?? throw new InvalidOperationException("ConnectionStrings:WhalePod is required.");

var ipHashSecret = builder.Configuration["Audit:IpHashSecret"];
if (!builder.Environment.IsDevelopment()
    && (string.IsNullOrWhiteSpace(ipHashSecret) || ipHashSecret.Length < 32))
{
    throw new InvalidOperationException("Audit:IpHashSecret must be at least 32 characters outside Development.");
}

var keysDirectory = builder.Configuration["DataProtection:KeysDirectory"];
if (string.IsNullOrWhiteSpace(keysDirectory))
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("DataProtection:KeysDirectory is required outside Development.");
    }

    keysDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "whalepod",
        "dpkeys");
}

Directory.CreateDirectory(keysDirectory);
builder.Services.AddDataProtection()
    .SetApplicationName("WhalePod")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

builder.Services.AddOpenApi();
builder.Services.AddWhalePodDbContext(connectionString);
builder.Services.AddWhalePodMediaStorage(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    builder.Environment.IsDevelopment());
builder.Services.AddWhalePodEmail(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    includeTransport: true);
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.Configure<DeviceVerificationOptions>(
    builder.Configuration.GetSection("DeviceVerification"));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeviceVerificationFailureTracker>();
builder.Services.AddScoped<DeviceVerificationService>();
builder.Services.AddHostedService<DeviceChallengeCleanupWorker>();
builder.Services.AddScoped<PublicContentService>();
builder.Services.AddScoped<PublicSearchService>();
builder.Services.AddScoped<AdminAccessService>();
builder.Services.AddScoped<ServiceAccountAdminService>();
builder.Services.AddScoped<AiSettingsService>();
builder.Services.AddScoped<AiBudgetService>();
builder.Services.AddScoped<AgentModerationService>();
builder.Services.AddScoped<AgentFailureRecorder>();
builder.Services.AddScoped<SiteModeService>();
builder.Services.AddScoped<ActorGuard>();
builder.Services.AddScoped<PostWriteService>();
builder.Services.AddScoped<CommentWriteService>();
builder.Services.AddScoped<LikeService>();
builder.Services.AddScoped<AcceptanceService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ContentModerationService>();
builder.Services.AddScoped<AdminUserService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<AuthorContentService>();
builder.Services.AddScoped<ImageUploadService>();
builder.Services.AddScoped<MediaCleanupService>();
builder.Services.AddScoped<RetentionCleanupService>();
builder.Services.AddScoped<ApiAuditContextFactory>();
builder.Services.AddHostedService<MediaUploadCleanupWorker>();
builder.Services.AddHostedService<EmailOutboxWorker>();
builder.Services.AddHostedService<RetentionCleanupWorker>();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxImageUploadRequestBytes;
    options.ValueCountLimit = 4;
    options.MultipartHeadersLengthLimit = 16 * 1024;
});

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-Token";
    options.Cookie.Name = "whalepod_csrf";
    options.Cookie.Path = "/";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "whalepod_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context => ApiEnvelope.WriteProblemAsync(
            context.HttpContext,
            StatusCodes.Status401Unauthorized,
            ApiErrorCodes.AuthRequired,
            "请先登录后再进行此操作。");
        options.Events.OnRedirectToAccessDenied = context => ApiEnvelope.WriteProblemAsync(
            context.HttpContext,
            StatusCodes.Status403Forbidden,
            ApiErrorCodes.Forbidden,
            "没有权限执行此操作。");
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AgentService", policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim("auth_type", "service"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api-write", context =>
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var partitionKey = string.IsNullOrWhiteSpace(userId)
            ? $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}"
            : $"user:{userId}";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = writeLimitPermit,
            Window = TimeSpan.FromSeconds(writeLimitWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.AddPolicy("api-upload", context =>
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var partitionKey = string.IsNullOrWhiteSpace(userId)
            ? $"upload-ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}"
            : $"upload-user:{userId}";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = uploadLimitPermit,
            Window = TimeSpan.FromSeconds(uploadLimitWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.AddPolicy("device-challenge", context =>
        DeviceRateLimitPartition(context, "device-challenge", deviceChallengeLimit));
    options.AddPolicy("device-complete", context =>
        DeviceRateLimitPartition(context, "device-complete", deviceCompleteLimit));
    options.AddPolicy("device-write", context =>
        DeviceRateLimitPartition(context, "device-write", deviceWriteLimit));
    options.AddPolicy("agent-read", context =>
        AgentRateLimitPartition(context, "agent-read", agentReadLimit));
    options.AddPolicy("agent-write", context =>
        AgentRateLimitPartition(context, "agent-write", agentWriteLimit));
    options.AddPolicy("agent-heartbeat", context =>
        AgentRateLimitPartition(context, "agent-heartbeat", agentHeartbeatLimit));
    options.OnRejected = (context, cancellationToken) =>
        WriteRateLimitProblemAsync(context, writeLimitWindowSeconds, cancellationToken);
});

var app = builder.Build();

if (forwardedHeadersEnabled)
{
    app.UseForwardedHeaders();
}

var requestUserHashKey = string.IsNullOrWhiteSpace(ipHashSecret)
    ? null
    : Encoding.UTF8.GetBytes(ipHashSecret);
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
    var started = Stopwatch.GetTimestamp();
    try
    {
        await next();
    }
    finally
    {
        var elapsedMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
        app.Logger.LogInformation(
            "http_request method={Method} path={Path} status={Status} elapsedMs={ElapsedMs} requestId={RequestId} userRef={UserRef}",
            context.Request.Method,
            context.Request.Path.Value,
            context.Response.StatusCode,
            elapsedMs,
            context.TraceIdentifier,
            UserReference(context, requestUserHashKey));
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

UseLocalMediaStaticFiles(app);

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ApiProblemException exception) when (!context.Response.HasStarted)
    {
        if (exception.RetryAfterSeconds is int retryAfterSeconds)
        {
            context.Response.Headers.RetryAfter =
                retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        await ApiEnvelope.WriteProblemAsync(
            context,
            exception.StatusCode,
            exception.Code,
            exception.Message,
            exception.Details);
    }
    catch (Exception exception) when (!context.Response.HasStarted
        && exception is not OperationCanceledException)
    {
        app.Logger.LogError(exception, "API request failed.");
        await ApiEnvelope.WriteProblemAsync(
            context,
            StatusCodes.Status500InternalServerError,
            "INTERNAL",
            "服务端错误，请稍后重试。");
    }
});

UseApiBodySizeLimit(app, maxJsonBodyBytes, maxImageUploadRequestBytes);

app.UseAuthentication();
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api/v1/agent"),
    branch => branch.UseMiddleware<AgentTokenMiddleware>());
app.UseMiddleware<SiteReadOnlyApiMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();
app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "whalepod-api"
}));

app.MapGet("/healthz/ready", async (WhalePodDbContext db, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    try
    {
        return await db.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "ready", database = "ok" })
            : Results.Json(new { status = "not_ready", database = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Database readiness check failed.");
        return Results.Json(new { status = "not_ready", database = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

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

    var result = await search.SearchAsync(
        normalizedQuery,
        normalizedType,
        parsedLimit,
        cursor,
        cancellationToken);

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
});

app.MapContentWriteEndpoints();
app.MapDeviceEndpoints();
app.MapAgentEndpoints();
app.MapAdminEndpoints();
app.MapMeEndpoints();
app.MapNotificationEndpoints();
app.MapUploadEndpoints();

app.Run();

static RateLimitPartition<string> DeviceRateLimitPartition(
    HttpContext context,
    string policy,
    int permitLimit)
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    var partitionKey = string.IsNullOrWhiteSpace(userId)
        ? $"{policy}-ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}"
        : $"{policy}-user:{userId}";
    return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    });
}

static string? UserReference(HttpContext context, byte[]? hashKey)
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return null;
    }

    var userBytes = Encoding.UTF8.GetBytes(userId);
    var hash = hashKey is null
        ? SHA256.HashData(userBytes)
        : HMACSHA256.HashData(hashKey, userBytes);
    return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
}

static RateLimitPartition<string> AgentRateLimitPartition(
    HttpContext context,
    string policy,
    int permitLimit)
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    var partitionKey = string.IsNullOrWhiteSpace(userId)
        ? $"{policy}-ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}"
        : $"{policy}-service:{userId}";
    return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    });
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

static void ConfigureForwardedHeaders(WebApplicationBuilder builder)
{
    var forwardLimit = builder.Configuration.GetValue("ReverseProxy:ForwardLimit", 1);
    if (forwardLimit < 1)
    {
        throw new InvalidOperationException("ReverseProxy:ForwardLimit must be positive.");
    }

    var configuredProxies = builder.Configuration
        .GetSection("ReverseProxy:KnownProxies")
        .Get<string[]>() ?? [];
    var configuredNetworks = builder.Configuration
        .GetSection("ReverseProxy:KnownNetworks")
        .Get<string[]>() ?? [];
    if (configuredProxies.Length == 0 && configuredNetworks.Length == 0)
    {
        throw new InvalidOperationException(
            "ReverseProxy:KnownProxies or ReverseProxy:KnownNetworks is required when forwarded headers are enabled.");
    }

    var knownProxies = new List<IPAddress>();
    foreach (var configuredProxy in configuredProxies)
    {
        if (!IPAddress.TryParse(configuredProxy, out var proxy))
        {
            throw new InvalidOperationException($"ReverseProxy:KnownProxies contains an invalid IP address: {configuredProxy}");
        }

        knownProxies.Add(proxy);
    }

    var knownNetworks = configuredNetworks
        .Select(ParseKnownNetwork)
        .ToList();

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = forwardLimit;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in knownProxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (var network in knownNetworks)
        {
            options.KnownIPNetworks.Add(network);
        }
    });
}

static System.Net.IPNetwork ParseKnownNetwork(string value)
{
    var parts = value.Split('/', 2);
    if (!IPAddress.TryParse(parts[0], out var address))
    {
        throw new InvalidOperationException($"ReverseProxy:KnownNetworks contains an invalid network: {value}");
    }

    var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
    var prefix = maxPrefix;
    if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > maxPrefix))
    {
        throw new InvalidOperationException($"ReverseProxy:KnownNetworks contains an invalid prefix: {value}");
    }

    return new System.Net.IPNetwork(address, prefix);
}

static void UseApiBodySizeLimit(WebApplication app, long maxJsonBytes, long maxImageUploadBytes)
{
    app.Use(async (context, next) =>
    {
        if (!IsApiWriteRequest(context.Request))
        {
            await next();
            return;
        }

        var isImageUpload = IsImageUploadRequest(context.Request);
        var maxBytes = isImageUpload ? maxImageUploadBytes : maxJsonBytes;
        if (context.Request.ContentLength is long declaredLength && declaredLength > maxBytes)
        {
            if (isImageUpload)
            {
                await WriteUploadTooLargeAsync(context);
            }
            else
            {
                await WriteBodyTooLargeAsync(context, maxBytes);
            }

            return;
        }

        context.Request.Body = new SizeLimitedRequestBodyStream(context.Request.Body, maxBytes);
        try
        {
            await next();
        }
        catch (RequestBodyTooLargeException) when (!context.Response.HasStarted)
        {
            if (isImageUpload)
            {
                await WriteUploadTooLargeAsync(context);
            }
            else
            {
                await WriteBodyTooLargeAsync(context, maxBytes);
            }
        }
    });
}

static bool IsImageUploadRequest(HttpRequest request)
{
    return request.Path.StartsWithSegments("/api/v1/uploads/images")
        && HttpMethods.IsPost(request.Method);
}

static void UseLocalMediaStaticFiles(WebApplication app)
{
    var options = app.Services.GetRequiredService<IOptions<MediaStorageOptions>>().Value;
    if (!string.Equals(options.Provider, MediaStorageProviders.Local, StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(options.LocalRootPath))
    {
        return;
    }

    var contentTypes = new FileExtensionContentTypeProvider();
    contentTypes.Mappings[".webp"] = "image/webp";
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(options.LocalRootPath),
        RequestPath = options.PublicBaseUrl,
        ContentTypeProvider = contentTypes,
        OnPrepareResponse = context =>
        {
            context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        }
    });
}

static bool IsApiWriteRequest(HttpRequest request)
{
    return request.Path.StartsWithSegments("/api/v1")
        && !HttpMethods.IsGet(request.Method)
        && !HttpMethods.IsHead(request.Method)
        && !HttpMethods.IsOptions(request.Method)
        && !HttpMethods.IsTrace(request.Method);
}

static Task WriteBodyTooLargeAsync(HttpContext context, long maxBytes)
{
    return ApiEnvelope.WriteProblemAsync(
        context,
        StatusCodes.Status400BadRequest,
        ApiErrorCodes.ValidationFailed,
        "请求体超过大小上限。",
        new { field = "body", reason = "body_too_large", maxBytes });
}

static Task WriteUploadTooLargeAsync(HttpContext context)
{
    return ApiEnvelope.WriteProblemAsync(
        context,
        StatusCodes.Status413PayloadTooLarge,
        ApiErrorCodes.UploadTooLarge,
        "图片不能超过 10 MB。",
        new { field = "file", maxBytes = MediaLimits.MaxImageBytes });
}

static ValueTask WriteRateLimitProblemAsync(
    OnRejectedContext context,
    int fallbackSeconds,
    CancellationToken cancellationToken)
{
    var retryAfterSeconds = fallbackSeconds;
    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
    {
        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
    }

    context.HttpContext.Response.Headers.RetryAfter =
        retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
    var isAgent = context.HttpContext.Request.Path.StartsWithSegments("/api/v1/agent");
    return new ValueTask(ApiEnvelope.WriteProblemAsync(
        context.HttpContext,
        StatusCodes.Status429TooManyRequests,
        isAgent ? ApiErrorCodes.AgentRateLimited : ApiErrorCodes.RateLimited,
        isAgent ? "Agent 请求过于频繁，请按 Retry-After 退避。" : "请求过于频繁，请稍后重试。",
        new { retryAfterSeconds }));
}

internal sealed class RequestBodyTooLargeException : Exception;

internal sealed class SizeLimitedRequestBodyStream(Stream inner, long maxBytes) : Stream
{
    private long _bytesRead;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Count(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Count(read);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
        Count(read);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Count(int read)
    {
        if (read <= 0)
        {
            return;
        }

        _bytesRead += read;
        if (_bytesRead > maxBytes)
        {
            throw new RequestBodyTooLargeException();
        }
    }
}

public partial class Program;