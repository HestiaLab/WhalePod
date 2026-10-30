using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using System.Xml.Linq;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

using WhalePod.Contracts.Devices;
using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;
using WhalePod.Infrastructure.Storage;
using WhalePod.Web;
using WhalePod.Web.Components;
using WhalePod.Web.Endpoints;
using WhalePod.Web.Security;
using WhalePod.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var forwardedHeadersEnabled = builder.Configuration.GetValue("ReverseProxy:ForwardedHeadersEnabled", false);
if (forwardedHeadersEnabled)
{
    ConfigureForwardedHeaders(builder);
}

var connectionString = builder.Configuration.GetConnectionString("WhalePod")
    ?? throw new InvalidOperationException("ConnectionStrings:WhalePod is required.");

var publicSiteBaseUrl = builder.Configuration["PublicSite:BaseUrl"];
if (!Uri.TryCreate(publicSiteBaseUrl, UriKind.Absolute, out var publicSiteUri)
    || publicSiteUri.Scheme is not ("http" or "https"))
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("PublicSite:BaseUrl must be an absolute HTTP(S) URL outside Development.");
    }
}

var ipHashSecret = builder.Configuration["Audit:IpHashSecret"];
if (!builder.Environment.IsDevelopment()
    && (string.IsNullOrWhiteSpace(ipHashSecret) || ipHashSecret.Length < 32))
{
    throw new InvalidOperationException("Audit:IpHashSecret must be at least 32 characters outside Development.");
}

if (!builder.Environment.IsDevelopment())
{
    RequireSecret(builder.Configuration, "ViewCounter:DedupeSecret");
    RequireSecret(builder.Configuration, "CopyCounter:DedupeSecret");
    if (builder.Configuration.GetValue("Auth:DevLoginEnabled", false))
    {
        throw new InvalidOperationException("Auth:DevLoginEnabled must be false outside Development.");
    }

    if (string.IsNullOrWhiteSpace(builder.Configuration["GitHub:ClientId"])
        || string.IsNullOrWhiteSpace(builder.Configuration["GitHub:ClientSecret"]))
    {
        throw new InvalidOperationException(
            "GitHub:ClientId and GitHub:ClientSecret must be configured outside Development.");
    }

    if (builder.Configuration.GetValue("Account:GitHubHmacBlacklistEnabled", false))
    {
        RequireSecret(builder.Configuration, "Account:GitHubHmacSecret");
    }
}

var viewCounterWindowSeconds = builder.Configuration.GetValue("ViewCounter:DedupeWindowSeconds", 600);
var copyCounterBucketSeconds = builder.Configuration.GetValue("CopyCounter:BucketSeconds", 600);
var publicSearchLimit = builder.Configuration.GetValue("PublicSearch:RateLimitPerMinute", 30);
var publicSearchTimeoutSeconds = builder.Configuration.GetValue("PublicSearch:TimeoutSeconds", 3);
if (viewCounterWindowSeconds < 60
    || copyCounterBucketSeconds < 60
    || publicSearchLimit < 1
    || publicSearchTimeoutSeconds < 1)
{
    throw new InvalidOperationException("ViewCounter, CopyCounter and PublicSearch values must be positive.");
}

var webWriteLimitPermit = builder.Configuration.GetValue("WebWriteRateLimit:PermitLimit", 60);
var webWriteLimitWindowSeconds = builder.Configuration.GetValue("WebWriteRateLimit:WindowSeconds", 60);
var maxFormBodyBytes = builder.Configuration.GetValue("WebRequestLimits:MaxFormBodyBytes", 2 * 1024 * 1024);
var absoluteSessionHours = builder.Configuration.GetValue("Auth:AbsoluteSessionHours", 24);
if (absoluteSessionHours is < 1 or > 168)
{
    throw new InvalidOperationException("Auth:AbsoluteSessionHours must be between 1 and 168.");
}
var maxImageUploadBytes = builder.Configuration.GetValue(
    "WebRequestLimits:MaxImageUploadBytes",
    MediaLimits.MaxImageBytes + 1024 * 1024);
var inviteRedeemLimit = builder.Configuration.GetValue("InviteRedeemRateLimit:PermitLimit", 10);
var inviteRedeemWindowSeconds = builder.Configuration.GetValue("InviteRedeemRateLimit:WindowSeconds", 60);
if (webWriteLimitPermit < 1
    || webWriteLimitWindowSeconds < 1
    || maxFormBodyBytes < 1024
    || maxImageUploadBytes < MediaLimits.MaxImageBytes + 1024 * 1024
    || inviteRedeemLimit < 1
    || inviteRedeemWindowSeconds < 1)
{
    throw new InvalidOperationException(
        "WebWriteRateLimit, WebRequestLimits and InviteRedeemRateLimit values must be positive, and the image upload limit must cover the maximum image size.");
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

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddWhalePodDbContext(connectionString);
builder.Services.AddWhalePodMediaStorage(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    builder.Environment.IsDevelopment());
builder.Services.AddWhalePodEmail(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    includeTransport: false);
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<InviteRedemptionService>();
builder.Services.AddScoped<CopyCounterService>();
builder.Services.AddScoped<ViewCounterService>();
builder.Services.AddScoped<AdminAccessService>();
builder.Services.AddScoped<SessionValidator>();
builder.Services.AddScoped<ActorGuard>();
builder.Services.AddScoped<AdminBootstrapService>();
builder.Services.AddScoped<SiteModeService>();
builder.Services.AddScoped<TagAdminService>();
builder.Services.AddScoped<CategoryAdminService>();
builder.Services.AddScoped<ContentModerationService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<AdminUserService>();
builder.Services.AddScoped<InviteAdminService>();
builder.Services.AddScoped<AuditAdminService>();
builder.Services.AddScoped<ProfileSettingsService>();
builder.Services.AddScoped<ImageUploadService>();
builder.Services.Configure<GitHubOAuthOptions>(builder.Configuration.GetSection("GitHub"));
builder.Services.AddScoped<GitHubOAuthStateProtector>();
builder.Services.AddHttpClient<GitHubOAuthService>();
builder.Services.AddScoped<ServiceAccountAdminService>();
builder.Services.AddScoped<AiSettingsService>();
builder.Services.AddScoped<AiBudgetService>();
builder.Services.AddScoped<AiQualityService>();
builder.Services.AddScoped<SensitiveWordAdminService>();
builder.Services.AddScoped<SystemHealthService>();
builder.Services.AddScoped<AccountDeletionService>();
builder.Services.AddScoped<AppealService>();
builder.Services.AddScoped<PublicContentService>();
builder.Services.AddScoped<PublicSearchService>();
builder.Services.Configure<PublicSearchOptions>(builder.Configuration.GetSection("PublicSearch"));
builder.Services.AddScoped<PublicDiscoveryService>();
builder.Services.AddScoped<PostWriteService>();
builder.Services.AddScoped<CommentWriteService>();
builder.Services.AddScoped<LikeService>();
builder.Services.AddScoped<AcceptanceService>();
builder.Services.AddSingleton<PublicSiteLinks>();
builder.Services.Configure<AccountSecurityOptions>(builder.Configuration.GetSection("Account"));
builder.Services.AddScoped<AuditRequestContextFactory>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<InviteRevealCache>();
builder.Services.AddSingleton<ServiceTokenRevealCache>();
builder.Services.AddSingleton<EditorDraftStore>();
builder.Services.AddSingleton<CommentDraftStore>();
builder.Services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<CopyCounterOptions>(builder.Configuration.GetSection("CopyCounter"));
builder.Services.Configure<ViewCounterOptions>(builder.Configuration.GetSection("ViewCounter"));
builder.Services.Configure<DeviceVerificationOptions>(
    builder.Configuration.GetSection("DeviceVerification"));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));
builder.Services.Configure<HealthOptions>(builder.Configuration.GetSection("Health"));
builder.Services.AddSingleton<BackupStatusProvider>();
builder.Services.AddSingleton<DeviceVerificationFailureTracker>();
builder.Services.AddScoped<DeviceVerificationService>();
builder.Services.AddSingleton<DeviceChallengeRevealCache>();
builder.Services.Configure<AdminBootstrapOptions>(builder.Configuration.GetSection("Auth"));
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
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = Math.Max(maxFormBodyBytes, maxImageUploadBytes);
    options.ValueLengthLimit = (int)Math.Min(maxFormBodyBytes, int.MaxValue);
    options.ValueCountLimit = 1024;
    options.KeyLengthLimit = 512;
    options.MultipartHeadersLengthLimit = 16 * 1024;
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/activate";
        options.Cookie.Name = "whalepod_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            var validator = context.HttpContext.RequestServices.GetRequiredService<SessionValidator>();
            if (!await validator.IsValidAsync(context.Principal, context.HttpContext.RequestAborted))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy
        .RequireAuthenticatedUser()
        .AddRequirements(new AdminRequirement()));
});
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

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null)
        {
            return RateLimitPartition.GetNoLimiter("explicit-policy");
        }

        if (!IsWebWriteRequest(context.Request))
        {
            return RateLimitPartition.GetNoLimiter("web-read");
        }

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var partitionKey = string.IsNullOrWhiteSpace(userId)
            ? $"web-write-ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}"
            : $"web-write-user:{userId}";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = webWriteLimitPermit,
            Window = TimeSpan.FromSeconds(webWriteLimitWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.AddPolicy("copy-counter", context =>
    {
        var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("CopyCounter:RateLimitPerMinute", 60),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.AddPolicy("invite-redeem", context =>
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var partitionKey = $"redeem:{(string.IsNullOrWhiteSpace(userId) ? "anonymous" : userId)}:{address}";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = inviteRedeemLimit,
            Window = TimeSpan.FromSeconds(inviteRedeemWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.AddPolicy("public-search", context =>
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var partitionKey = string.IsNullOrWhiteSpace(userId)
            ? $"search-ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}"
            : $"search-user:{userId}";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("PublicSearch:RateLimitPerMinute", 30),
            Window = TimeSpan.FromMinutes(1),
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
    options.OnRejected = async (context, cancellationToken) =>
    {
        var response = context.HttpContext.Response;
        response.Headers.RetryAfter = "60";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.ContentType = "text/html; charset=utf-8";
        await response.WriteAsync(
            StatusPageHtml(
                StatusCodes.Status429TooManyRequests,
                "请求过于频繁",
                "请求过于频繁，请稍后重试。"),
            cancellationToken);
    };
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    var allowedHosts = builder.Configuration["AllowedHosts"];
    if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Contains('*'))
    {
        throw new InvalidOperationException(
            "AllowedHosts must list explicit production host names.");
    }
}

if (forwardedHeadersEnabled)
{
    app.UseForwardedHeaders();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "SAMEORIGIN";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Content-Security-Policy-Report-Only"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https:; " +
        "font-src 'self' data:; connect-src 'self' ws: wss:; object-src 'none'; " +
        "base-uri 'self'; form-action 'self'; frame-ancestors 'self'";
    await next();
});

var requestUserHashKey = string.IsNullOrWhiteSpace(ipHashSecret)
    ? null
    : Encoding.UTF8.GetBytes(ipHashSecret);
app.Use(async (context, next) =>
{
    if (ShouldSkipRequestLog(context.Request.Path))
    {
        await next();
        return;
    }

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

UseFormBodySizeLimit(app, maxFormBodyBytes, maxImageUploadBytes);
UseLocalMediaStaticFiles(app);

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin"))
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Robots-Tag"] = "noindex";
    }

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStatusCodePagesWithReExecute("/status/{0}");
app.UseAuthentication();
app.UseMiddleware<SiteReadOnlyMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<ActivationGateMiddleware>();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "whalepod-web"
}));

app.MapGet("/healthz/ready", async (
    WhalePodDbContext db,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var readiness = await DatabaseReadiness.CheckAsync(db, cancellationToken);
    if (readiness.Ready)
    {
        return Results.Ok(new { status = "ready", database = "ok" });
    }

    logger.LogWarning("Database readiness check failed: {Reason}.", readiness.Reason);
    return Results.Json(
        new { status = "not_ready", database = "unavailable", reason = readiness.Reason },
        statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/dev/test-login", async (
    HttpContext http,
    WhalePodDbContext db,
    IConfiguration configuration,
    AdminBootstrapService adminBootstrap,
    SiteModeService siteMode,
    AuditRequestContextFactory auditContextFactory) =>
{
    if (!app.Environment.IsDevelopment() || !configuration.GetValue<bool>("Auth:DevLoginEnabled"))
    {
        return Results.NotFound();
    }

    if (await siteMode.IsLoginDisabledAsync(http.RequestAborted))
    {
        return Results.Redirect("/activate?error=login_disabled");
    }

    var handle = http.Request.Query["handle"].ToString();
    if (!Regex.IsMatch(handle, "^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$"))
    {
        handle = "dev-user";
    }

    var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Handle == handle, http.RequestAborted);
    if (user is null)
    {
        user = new User
        {
            Handle = handle,
            DisplayName = "开发测试用户"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(http.RequestAborted);
    }

    if (user.Status is not (UserStatuses.Active or UserStatuses.Muted))
    {
        return Results.Forbid();
    }

    await adminBootstrap.EnsureAdminAsync(user, auditContextFactory.Create(user.Id), http.RequestAborted);

    var issuedAt = DateTimeOffset.UtcNow;
    var absoluteExpiry = issuedAt.AddHours(absoluteSessionHours);
    var identity = new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Handle),
        new Claim(ClaimTypes.Role, user.Role),
        new Claim(SessionClaims.Version, user.SessionVersion.ToString(CultureInfo.InvariantCulture)),
        new Claim(SessionClaims.AbsoluteExpiry, absoluteExpiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        new Claim("auth_time", issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
    ], CookieAuthenticationDefaults.AuthenticationScheme);

    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = absoluteExpiry
        });
    return Results.Redirect(user.ActivatedAt is null ? "/activate" : "/");
});

app.MapPost("/activate/redeem", async (
    HttpContext http,
    IAntiforgery antiforgery,
    InviteRedemptionService redemptionService) =>
{
    if (http.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    try
    {
        await antiforgery.ValidateRequestAsync(http);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest();
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Unauthorized();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var outcome = await redemptionService.RedeemAsync(userId, form["code"].ToString(), http.RequestAborted);

    return outcome == InviteRedemptionOutcome.Success
        ? Results.Redirect("/")
        : Results.Redirect("/activate?error=invalid");
}).RequireAuthorization().RequireRateLimiting("invite-redeem");

app.MapPost("/logout", async (
    HttpContext http,
    IAntiforgery antiforgery,
    WhalePodDbContext db) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(http);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest();
    }

    if (Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        var now = DateTimeOffset.UtcNow;
        await db.Users
            .Where(user => user.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(user => user.SessionVersion, user => user.SessionVersion + 1)
                    .SetProperty(user => user.UpdatedAt, now),
                http.RequestAborted);
    }

    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
}).RequireAuthorization();


app.MapPost("/internal/counters/copy", async (
    HttpContext http,
    CopyCounterService counterService,
    ILogger<Program> logger) =>
{
    if (!IsSameOrigin(http))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    CopyCounterRequest? request;
    try
    {
        request = await http.Request.ReadFromJsonAsync<CopyCounterRequest>(http.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest();
    }

    if (request is null || request.ArtifactId == Guid.Empty)
    {
        return Results.BadRequest();
    }

    try
    {
        await counterService.RecordCopyAsync(request.ArtifactId, http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Copy counter failed.");
    }

    return Results.NoContent();
}).DisableAntiforgery().RequireRateLimiting("copy-counter");

app.MapPost("/posts/{postId:guid}/comments", async (
    Guid postId,
    HttpContext http,
    IAntiforgery antiforgery,
    CommentWriteService comments,
    CommentDraftStore drafts,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Redirect($"/posts/{postId}?error=invalid");
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var bodyMd = form["bodyMd"].ToString();
    Guid? parentId = null;
    if (!string.IsNullOrWhiteSpace(form["parentId"]))
    {
        if (!Guid.TryParse(form["parentId"], out var parsedParent))
        {
            drafts.Save(userId, postId, null, bodyMd);
            return Results.Redirect($"/posts/{postId}?error=invalid&restored=1");
        }

        parentId = parsedParent;
    }

    try
    {
        await comments.CreateAsync(
            userId,
            postId,
            new CommentCreateCommand(bodyMd, parentId),
            auditContextFactory.Create(userId),
            http.RequestAborted);
        drafts.Clear(userId);
        return Results.Redirect($"/posts/{postId}?message=pending");
    }
    catch (ApiProblemException exception)
    {
        drafts.Save(userId, postId, parentId, bodyMd);
        return Results.Redirect($"/posts/{postId}?error={MapCommentError(exception)}&restored=1");
    }
}).RequireAuthorization();

app.MapPost("/editor/create", async (
    HttpContext http,
    IAntiforgery antiforgery,
    PostWriteService posts,
    EditorDraftStore drafts,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Redirect("/editor?error=auth");
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    if (!Guid.TryParse(form["categoryId"], out var categoryId))
    {
        SaveEditorSnapshot(drafts, userId, form);
        return Results.Redirect("/editor?error=validation&field=categoryId&restored=1");
    }

    var imageIdValues = form["imageIds"];
    var imageAltValues = form["imageAlt"];
    List<PostImageInput>? images = null;
    if (imageIdValues.Count != imageAltValues.Count)
    {
        SaveEditorSnapshot(drafts, userId, form);
        return Results.Redirect("/editor?error=validation&field=imageIds&restored=1");
    }

    if (imageIdValues.Count > 10)
    {
        SaveEditorSnapshot(drafts, userId, form);
        return Results.Redirect("/editor?error=validation&field=images&restored=1");
    }

    if (imageIdValues.Count > 0)
    {
        images = new List<PostImageInput>(imageIdValues.Count);
        for (var index = 0; index < imageIdValues.Count; index++)
        {
            if (!Guid.TryParse(imageIdValues[index], out var imageId))
            {
                SaveEditorSnapshot(drafts, userId, form);
                return Results.Redirect("/editor?error=validation&field=imageIds&restored=1");
            }

            var alt = FormValue(form, "imageAlt", index).Trim();
            images.Add(new PostImageInput(imageId, alt.Length == 0 ? null : alt));
        }

        if (images.Select(image => image.Id).Distinct().Count() != images.Count)
        {
            SaveEditorSnapshot(drafts, userId, form);
            return Results.Redirect("/editor?error=validation&field=imageIds&restored=1");
        }
    }

    var artifactCount = Math.Max(
        Math.Max(form["artifactKind"].Count, form["artifactName"].Count),
        Math.Max(form["artifactNote"].Count, form["artifactContent"].Count));
    if (artifactCount > 5)
    {
        SaveEditorSnapshot(drafts, userId, form);
        return Results.Redirect("/editor?error=validation&field=artifacts&restored=1");
    }

    List<ArtifactInput>? artifacts = null;
    for (var index = 0; index < artifactCount; index++)
    {
        var kind = FormValue(form, "artifactKind", index);
        var name = FormValue(form, "artifactName", index).Trim();
        var note = FormValue(form, "artifactNote", index);
        var content = FormValue(form, "artifactContent", index);
        if (kind.Length == 0 && name.Length == 0 && note.Length == 0 && content.Length == 0)
        {
            continue;
        }

        artifacts ??= [];
        artifacts.Add(new ArtifactInput(
            kind,
            name,
            string.IsNullOrWhiteSpace(note) ? null : note,
            content));
    }

    var tags = form["tags"]
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToList();
    var kernel = form["kernel"].ToString().Trim();
    var platform = form["platform"].ToString().Trim();
    var model = form["model"].ToString().Trim();
    var environment = kernel.Length == 0 && platform.Length == 0 && model.Length == 0
        ? null
        : new EnvironmentInput(
            kernel.Length == 0 ? null : kernel,
            platform.Length == 0 ? null : platform,
            model.Length == 0 ? null : model);

    try
    {
        var created = await posts.CreateAsync(
            userId,
            new PostCreateCommand(
                form["title"].ToString(),
                categoryId,
                form["bodyMd"].ToString(),
                tags.Count == 0 ? null : tags,
                environment,
                artifacts,
                images),
            auditContextFactory.Create(userId),
            http.RequestAborted);
        drafts.Clear(userId);
        return Results.Redirect($"/posts/{created.Id}?message=post-pending");
    }
    catch (ApiProblemException exception)
    {
        SaveEditorSnapshot(drafts, userId, form);
        return exception.Code switch
        {
            ApiErrorCodes.AccountMuted => Results.Redirect("/editor?error=muted&restored=1"),
            ApiErrorCodes.AccountBanned => Results.Redirect("/editor?error=banned&restored=1"),
            ApiErrorCodes.Forbidden => Results.Redirect("/editor?error=auth&restored=1"),
            ApiErrorCodes.ValidationFailed => Results.Redirect(
                $"/editor?error=validation&field={Uri.EscapeDataString(WebEndpointHelpers.DetailField(exception) ?? "unknown")}&restored=1"),
            _ => Results.Redirect("/editor?error=invalid&restored=1")
        };
    }
}).RequireAuthorization();

app.MapPost("/editor/preview", async (
    HttpContext http,
    IAntiforgery antiforgery,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out _))
    {
        return Results.Unauthorized();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var body = form["bodyMd"].ToString();
    if (RuneHelper.Count(body) > 60_000)
    {
        return Results.BadRequest("正文不能超过 60,000 字。");
    }

    http.Response.Headers.CacheControl = "no-store";
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.Content(MarkdownRenderer.Render(body).Html, "text/html; charset=utf-8");
}).RequireAuthorization();

app.MapAdminConfigEndpoints();
app.MapAdminConsoleEndpoints();
app.MapDiscoveryEndpoints();
app.MapAccountEndpoints();
app.MapInteractionEndpoints();
app.MapEditorEndpoints();
app.MapGitHubOAuthEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

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

static void RequireSecret(IConfiguration configuration, string key)
{
    var value = configuration[key];
    if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
    {
        throw new InvalidOperationException($"{key} must be at least 32 characters outside Development.");
    }
}

static bool IsWebWriteRequest(HttpRequest request)
{
    if (request.Path.StartsWithSegments("/_blazor"))
    {
        return false;
    }

    return !HttpMethods.IsGet(request.Method)
        && !HttpMethods.IsHead(request.Method)
        && !HttpMethods.IsOptions(request.Method)
        && !HttpMethods.IsTrace(request.Method);
}

static bool ShouldSkipRequestLog(PathString path)
{
    return path.StartsWithSegments("/_blazor")
        || path.StartsWithSegments("/_framework")
        || path.StartsWithSegments("/media")
        || path.StartsWithSegments("/favicon")
        || path.StartsWithSegments("/app.css")
        || path.StartsWithSegments("/WhalePod.Web.styles.css");
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

static void UseFormBodySizeLimit(WebApplication app, long maxBytes, long maxImageBytes)
{
    app.Use(async (context, next) =>
    {
        if (!IsWebWriteRequest(context.Request))
        {
            await next();
            return;
        }

        var limit = context.Request.Path.StartsWithSegments("/editor/images") ? maxImageBytes : maxBytes;
        if (context.Request.ContentLength is long declaredLength && declaredLength > limit)
        {
            await WriteFormBodyTooLargeAsync(context);
            return;
        }

        context.Request.Body = new SizeLimitedWebRequestBodyStream(context.Request.Body, limit);
        try
        {
            await next();
        }
        catch (WebRequestBodyTooLargeException) when (!context.Response.HasStarted)
        {
            await WriteFormBodyTooLargeAsync(context);
        }
    });
}

static async Task WriteFormBodyTooLargeAsync(HttpContext context)
{
    var isMultipart = context.Request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true;
    context.Response.StatusCode = isMultipart
        ? StatusCodes.Status413PayloadTooLarge
        : StatusCodes.Status400BadRequest;
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(
        StatusPageHtml(
            context.Response.StatusCode,
            "请求体过大",
            isMultipart
                ? "上传或提交的内容超过大小上限，请缩小后重试。"
                : "提交的内容超过大小上限，请缩小后重试。"),
        context.RequestAborted);
}

static string StatusPageHtml(int statusCode, string title, string message)
{
    var encodedTitle = WebUtility.HtmlEncode(title);
    var encodedMessage = WebUtility.HtmlEncode(message);
    return $"""
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <meta name="robots" content="noindex" />
          <meta name="color-scheme" content="light dark" />
          <title>{encodedTitle} - WhalePod</title>
          <link rel="stylesheet" href="/app.css" />
        </head>
        <body>
          <a class="skip-link" href="#main-content">跳到正文</a>
          <header class="site-header">
            <div class="site-header-inner">
              <a class="brand" href="/">
                <img class="brand-favicon" src="/favicon.svg" alt="" width="26" height="26" />
                <span class="brand-lockup"><span class="brand-word">WhalePod</span><span class="brand-cn">鲸群</span></span>
              </a>
            </div>
          </header>
          <main id="main-content" class="site-main" tabindex="-1">
            <div class="status-page">
              <p class="status-code">HTTP {statusCode}</p>
              <h1>{encodedTitle}</h1>
              <p>{encodedMessage}</p>
              <div class="status-actions">
                <a class="button" href="/">返回信息流</a>
                <a class="button button-secondary" href="/search">搜索内容</a>
              </div>
            </div>
          </main>
        </body>
        </html>
        """;
}



static string MapCommentError(ApiProblemException exception)
{
    if (exception.Code == ApiErrorCodes.AccountMuted)
    {
        return "muted";
    }

    if (exception.Code == ApiErrorCodes.Forbidden
        && WebEndpointHelpers.DetailReason(exception) == "activation_required")
    {
        return "activation";
    }

    return "invalid";
}











static string FormValue(IFormCollection form, string key, int index)
{
    var values = form[key];
    return index < values.Count ? values[index] ?? string.Empty : string.Empty;
}

static void SaveEditorSnapshot(EditorDraftStore drafts, Guid userId, IFormCollection form)
{
    var imageIds = form["imageIds"];
    var imageAlts = form["imageAlt"];
    var images = new List<EditorImageRef>();
    var imageCount = Math.Min(imageIds.Count, imageAlts.Count);
    for (var index = 0; index < imageCount; index++)
    {
        if (Guid.TryParse(imageIds[index], out var imageId))
        {
            images.Add(new EditorImageRef(imageId, imageAlts[index] ?? string.Empty));
        }
    }

    var artifacts = new List<EditorArtifact>(5);
    for (var index = 0; index < 5; index++)
    {
        artifacts.Add(new EditorArtifact(
            FormValue(form, "artifactKind", index),
            FormValue(form, "artifactName", index),
            FormValue(form, "artifactNote", index),
            FormValue(form, "artifactContent", index)));
    }

    var tags = form["tags"]
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .Take(20)
        .ToList();
    drafts.SaveSnapshot(userId, new EditorSnapshot(
        form["title"].ToString(),
        form["categoryId"].ToString(),
        form["bodyMd"].ToString(),
        form["kernel"].ToString(),
        form["platform"].ToString(),
        form["model"].ToString(),
        tags,
        artifacts,
        images));
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







static Task<IResult?> ValidateFormAsync(
    HttpContext http,
    IAntiforgery antiforgery,
    ILogger logger)
{
    return WebFormGuard.ValidateAsync(http, antiforgery, logger);
}

static bool IsSameOrigin(HttpContext context)
{
    return WebFormGuard.IsSameOrigin(context);
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

internal sealed record CopyCounterRequest(Guid ArtifactId);

internal sealed class WebRequestBodyTooLargeException : Exception;

internal sealed class SizeLimitedWebRequestBodyStream(Stream inner, long maxBytes) : Stream
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
            throw new WebRequestBodyTooLargeException();
        }
    }
}