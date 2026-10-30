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

app.MapPost("/admin/tags/{tagId:guid}/rename", async (
    Guid tagId,
    HttpContext http,
    IAntiforgery antiforgery,
    TagAdminService tagAdmin,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var outcome = await tagAdmin.RenameAsync(
        tagId,
        form["name"].ToString(),
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return outcome switch
    {
        TagMutationResult.Success => Results.Redirect("/admin/tags?message=renamed"),
        TagMutationResult.Unchanged => Results.Redirect("/admin/tags?message=unchanged"),
        TagMutationResult.DuplicateName => Results.Redirect("/admin/tags?error=duplicate"),
        TagMutationResult.InvalidName => Results.Redirect("/admin/tags?error=invalid-name"),
        TagMutationResult.NotFound => Results.NotFound(),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/tags/{tagId:guid}/visibility", async (
    Guid tagId,
    HttpContext http,
    IAntiforgery antiforgery,
    TagAdminService tagAdmin,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var hidden = bool.TryParse(form["hidden"].ToString(), out var requestedVisibility) && requestedVisibility;
    if (hidden && !form["confirmImpact"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest();
    }

    var outcome = await tagAdmin.SetVisibilityAsync(
        tagId,
        hidden,
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return outcome switch
    {
        TagMutationResult.Success => Results.Redirect(hidden
            ? "/admin/tags?message=hidden"
            : "/admin/tags?message=restored"),
        TagMutationResult.Unchanged => Results.Redirect("/admin/tags?message=unchanged"),
        TagMutationResult.NotFound => Results.NotFound(),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/invites/search", async (
    HttpContext http,
    IAntiforgery antiforgery,
    InviteAdminService inviteAdmin,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var inviteId = await inviteAdmin.FindInviteIdByCodeAsync(
        form["code"].ToString(),
        http.RequestAborted);

    return inviteId is null
        ? Results.Redirect("/admin/invites?error=not-found")
        : Results.Redirect($"/admin/invites/{inviteId}");
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/invites/create", async (
    HttpContext http,
    IAntiforgery antiforgery,
    InviteAdminService inviteAdmin,
    InviteRevealCache revealCache,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    if (!int.TryParse(form["maxUses"].ToString(), out var maxUses))
    {
        return Results.Redirect("/admin/invites?error=invalid");
    }

    DateTimeOffset? expiresAt = null;
    var expiresOn = form["expiresOn"].ToString();
    if (!string.IsNullOrWhiteSpace(expiresOn))
    {
        if (!DateOnly.TryParseExact(
                expiresOn,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var expiryDate))
        {
            return Results.Redirect("/admin/invites?error=invalid-expiry");
        }

        var localEndOfDay = expiryDate.ToDateTime(new TimeOnly(23, 59, 59));
        expiresAt = new DateTimeOffset(localEndOfDay, TimeSpan.FromHours(8));
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            return Results.Redirect("/admin/invites?error=invalid-expiry");
        }
    }

    var result = await inviteAdmin.CreateAsync(
        form["note"].ToString(),
        maxUses,
        expiresAt,
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return result.Outcome switch
    {
        InviteAdminOutcome.Success => StoreInviteReveal(revealCache, actorId, result),
        InviteAdminOutcome.InvalidInput => Results.Redirect("/admin/invites?error=invalid"),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/invites/disable", async (
    HttpContext http,
    IAntiforgery antiforgery,
    InviteAdminService inviteAdmin,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    if (!form["confirmDisable"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Redirect("/admin/invites?error=selection");
    }

    var inviteIds = new List<Guid>();
    foreach (var value in form["inviteIds"])
    {
        if (Guid.TryParse(value, out var inviteId))
        {
            inviteIds.Add(inviteId);
        }
    }

    var result = await inviteAdmin.DisableUnusedAsync(
        inviteIds,
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return result.Outcome switch
    {
        InviteAdminOutcome.Success => Results.Redirect(
            $"/admin/invites?message=disabled&disabled={result.Disabled}&skipped={result.Skipped}"),
        InviteAdminOutcome.NoSelection or InviteAdminOutcome.InvalidInput =>
            Results.Redirect("/admin/invites?error=selection"),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/settings/profile/update", async (
    HttpContext http,
    IAntiforgery antiforgery,
    ProfileSettingsService profileSettings,
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
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var outcome = await profileSettings.UpdateProfileAsync(
        userId,
        form["displayName"].ToString(),
        form["bio"].ToString(),
        auditContextFactory.Create(userId),
        http.RequestAborted);

    return outcome switch
    {
        SettingsUpdateResult.Success => Results.Redirect("/settings/profile?message=updated"),
        SettingsUpdateResult.Unchanged => Results.Redirect("/settings/profile?message=unchanged"),
        SettingsUpdateResult.InvalidInput => Results.Redirect("/settings/profile?error=invalid"),
        SettingsUpdateResult.NotFound => Results.NotFound(),
        _ => Results.Forbid()
    };
}).RequireAuthorization();

app.MapPost("/settings/emails/update", async (
    HttpContext http,
    IAntiforgery antiforgery,
    ProfileSettingsService profileSettings,
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
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var repliesMentions = form["repliesMentions"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
    var moderationResults = form["moderationResults"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

    var outcome = await profileSettings.UpdateEmailPreferencesAsync(
        userId,
        repliesMentions,
        moderationResults,
        auditContextFactory.Create(userId),
        http.RequestAborted);

    return outcome switch
    {
        SettingsUpdateResult.Success => Results.Redirect("/settings/emails?message=updated"),
        SettingsUpdateResult.Unchanged => Results.Redirect("/settings/emails?message=unchanged"),
        SettingsUpdateResult.NotFound => Results.NotFound(),
        _ => Results.Forbid()
    };
}).RequireAuthorization();

app.MapPost("/settings/account/delete", async (
    HttpContext http,
    IAntiforgery antiforgery,
    AccountDeletionService deletion,
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
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    if (!form["confirmPermanent"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Redirect("/settings/account?error=confirm");
    }

    try
    {
        await deletion.DeleteAsync(
            userId,
            userId,
            form["deleteContent"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase),
            form["confirmHandle"].ToString(),
            auditContextFactory.Create(userId),
            http.RequestAborted);
    }
    catch (ApiProblemException exception)
    {
        return Results.Redirect($"/settings/account?error={MapAccountDeletionError(exception)}");
    }

    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/account/deleted");
}).RequireAuthorization();

app.MapPost("/settings/devices/challenge", async (
    HttpContext http,
    IAntiforgery antiforgery,
    DeviceVerificationService deviceVerification,
    DeviceChallengeRevealCache revealCache,
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
        return Results.Redirect("/activate");
    }

    try
    {
        var challenge = await deviceVerification.CreateChallengeAsync(
            userId,
            auditContextFactory.Create(userId),
            http.RequestAborted);
        revealCache.Store(userId, challenge);
        return Results.Redirect("/settings/devices?challenge=created");
    }
    catch (ApiProblemException exception)
    {
        return Results.Redirect($"/settings/devices?error={MapDeviceError(exception)}");
    }
}).RequireAuthorization().RequireRateLimiting("device-challenge");

app.MapPost("/settings/devices/complete", async (
    HttpContext http,
    IAntiforgery antiforgery,
    DeviceVerificationService deviceVerification,
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
        return Results.Redirect("/activate");
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var payload = form["payload"].ToString();
    var label = form["label"].ToString();
    DeviceCompleteRequest request;
    if (!string.IsNullOrWhiteSpace(payload))
    {
        if (payload.Length > 20_000 || !DevicePayloadParser.TryParse(payload, out var parsed) || parsed is null)
        {
            return Results.Redirect("/settings/devices?error=format");
        }

        request = parsed;
    }
    else
    {
        if (!long.TryParse(
                form["manualTimestamp"].ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var timestamp))
        {
            return Results.Redirect("/settings/devices?error=format");
        }

        request = new DeviceCompleteRequest(
            1,
            form["manualChallenge"].ToString(),
            string.IsNullOrWhiteSpace(form["manualHandle"])
                ? http.User.Identity?.Name
                : form["manualHandle"].ToString(),
            timestamp,
            form["manualKernelVersion"].ToString(),
            form["manualPlatform"].ToString(),
            form["manualPublicKey"].ToString(),
            form["manualSignature"].ToString(),
            null);
    }

    if (!string.IsNullOrWhiteSpace(label))
    {
        request = request with { Label = label };
    }

    try
    {
        await deviceVerification.CompleteAsync(
            userId,
            request,
            auditContextFactory.Create(userId),
            http.RequestAborted);
        return Results.Redirect("/settings/devices?message=verified");
    }
    catch (ApiProblemException exception)
    {
        return Results.Redirect($"/settings/devices?error={MapDeviceError(exception)}");
    }
}).RequireAuthorization().RequireRateLimiting("device-complete");

app.MapPost("/settings/devices/{deviceId:guid}/revoke", async (
    Guid deviceId,
    HttpContext http,
    IAntiforgery antiforgery,
    DeviceVerificationService deviceVerification,
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
        return Results.Redirect("/activate");
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    try
    {
        await deviceVerification.RevokeAsync(
            userId,
            deviceId,
            form["reason"].ToString(),
            auditContextFactory.Create(userId),
            http.RequestAborted);
        return Results.Redirect("/settings/devices?message=revoked");
    }
    catch (ApiProblemException exception)
    {
        return Results.Redirect($"/settings/devices?error={MapDeviceError(exception)}");
    }
}).RequireAuthorization().RequireRateLimiting("device-write");

app.MapPost("/appeals/submit", async (
    HttpContext http,
    IAntiforgery antiforgery,
    AppealService appeals,
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
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var targetType = form["targetType"].ToString();
    var targetIdText = form["targetId"].ToString().Trim();
    var targetId = userId;
    if (!targetType.Equals("account_penalty", StringComparison.OrdinalIgnoreCase)
        && !Guid.TryParse(targetIdText, out targetId))
    {
        return Results.Redirect("/appeals?error=target");
    }

    try
    {
        await appeals.SubmitAsync(
            userId,
            targetType,
            targetId,
            form["reason"].ToString(),
            auditContextFactory.Create(userId),
            http.RequestAborted);
        return Results.Redirect("/appeals?message=submitted");
    }
    catch (ApiProblemException exception)
    {
        return Results.Redirect($"/appeals?error={MapAppealSubmitError(exception)}");
    }
}).RequireAuthorization();

app.MapPost("/admin/appeals/{appealId}/review", async (
    string appealId,
    HttpContext http,
    IAntiforgery antiforgery,
    AppealService appeals,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
        || !Guid.TryParse(appealId, out var parsedAppealId))
    {
        return Results.NotFound();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    try
    {
        await appeals.ReviewAsync(
            adminId,
            parsedAppealId,
            new AppealReviewCommand(form["outcome"].ToString(), form["note"].ToString()),
            auditContextFactory.Create(adminId),
            http.RequestAborted);
        return Results.Redirect("/admin/appeals?message=reviewed");
    }
    catch (ApiProblemException exception)
    {
        return Results.Redirect($"/admin/appeals?error={MapAppealReviewError(exception)}");
    }
}).RequireAuthorization("AdminOnly");

app.MapGet("/email/unsubscribe", async (
    string? token,
    HttpContext http,
    EmailUnsubscribeService unsubscribe,
    EmailUnsubscribeTokenService tokens,
    AuditRequestContextFactory auditContextFactory) =>
{
    http.Response.Headers.CacheControl = "no-store";
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (!tokens.TryValidate(token, out var userId, out _, out _))
    {
        return UnsubscribeResult(StatusCodes.Status400BadRequest, "链接无效", "退订链接无效或已过期。");
    }

    var outcome = await unsubscribe.UnsubscribeAsync(token, auditContextFactory.Create(userId));
    return outcome switch
    {
        EmailUnsubscribeOutcome.Unsubscribed => UnsubscribeResult(
            StatusCodes.Status200OK,
            "退订成功",
            "此类邮件已退订；账号与安全邮件仍会发送。"),
        EmailUnsubscribeOutcome.AlreadyDisabled => UnsubscribeResult(
            StatusCodes.Status200OK,
            "已退订",
            "此类邮件此前已退订。"),
        EmailUnsubscribeOutcome.UserMissing => UnsubscribeResult(
            StatusCodes.Status404NotFound,
            "账号不可用",
            "账号不存在或已注销。"),
        _ => UnsubscribeResult(
            StatusCodes.Status400BadRequest,
            "链接无效",
            "退订链接无效或已过期。")
    };
});

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
                $"/editor?error=validation&field={Uri.EscapeDataString(DetailField(exception) ?? "unknown")}&restored=1"),
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

app.MapGet("/rss.xml", async (
    HttpContext http,
    PublicDiscoveryService discovery,
    PublicSiteLinks siteLinks) =>
{
    var document = await discovery.GetRssAsync(50, http.RequestAborted);
    if (TryWriteNotModified(http, document.LastModified, "rss"))
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    return Results.Text(
        RenderRss(document.Items, document.LastModified, siteLinks),
        "application/rss+xml; charset=utf-8");
});

app.MapGet("/sitemap.xml", async (
    HttpContext http,
    PublicDiscoveryService discovery,
    PublicSiteLinks siteLinks) =>
{
    var document = await discovery.GetSitemapIndexAsync(5000, http.RequestAborted);
    if (TryWriteNotModified(http, document.LastModified, "sitemap-index"))
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    return Results.Text(
        RenderSitemapIndex(document.Shards, siteLinks),
        "application/xml; charset=utf-8");
});

app.MapGet("/sitemap-posts-{page:int}.xml", async (
    int page,
    HttpContext http,
    PublicDiscoveryService discovery,
    PublicSiteLinks siteLinks) =>
{
    var document = await discovery.GetPostSitemapShardAsync(page, 5000, http.RequestAborted);
    if (document is null)
    {
        return Results.NotFound();
    }

    if (TryWriteNotModified(http, document.LastModified, $"sitemap-posts-{page}"))
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    return Results.Text(
        RenderSitemap(document.Entries, siteLinks),
        "application/xml; charset=utf-8");
});

app.MapGet("/sitemap-pages.xml", async (
    HttpContext http,
    PublicDiscoveryService discovery,
    PublicSiteLinks siteLinks) =>
{
    var document = await discovery.GetPageSitemapAsync(http.RequestAborted);
    if (TryWriteNotModified(http, document.LastModified, "sitemap-pages"))
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    return Results.Text(
        RenderSitemap(document.Entries, siteLinks),
        "application/xml; charset=utf-8");
});

app.MapGet("/robots.txt", (HttpContext http, PublicSiteLinks siteLinks) =>
{
    http.Response.Headers.CacheControl = "public, max-age=3600";
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    var robots = $"""
        User-agent: *
        Allow: /
        Disallow: /admin/
        Disallow: /settings/
        Disallow: /dev/
        Disallow: /internal/
        Disallow: /api/
        Disallow: /appeals
        Disallow: /email/unsubscribe
        Disallow: /editor
        Disallow: /search
        Sitemap: {siteLinks.Absolute("/sitemap.xml")}
        """;
    return Results.Text(robots, "text/plain; charset=utf-8");
});

app.MapPost("/admin/audit/export", async (
    HttpContext http,
    IAntiforgery antiforgery,
    AuditAdminService auditAdmin,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        await failure.ExecuteAsync(http);
        return;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        await Results.Forbid().ExecuteAsync(http);
        return;
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var today = AuditFilterBuilder.TodayInChina();
    var built = AuditFilterBuilder.Build(
        new AuditFilterInput(
            form["from"].ToString(),
            form["to"].ToString(),
            form["actor"].ToString(),
            form["action"].ToString(),
            form["targetType"].ToString(),
            form["targetId"].ToString()),
        today.AddDays(-6),
        today);

    var filter = built.Filter;
    if (filter is null)
    {
        http.Response.Redirect($"/admin/audit?error={built.Error}");
        return;
    }

    if (!AuditAdminService.IsExportRangeValid(filter))
    {
        http.Response.Redirect("/admin/audit?error=range-too-large");
        return;
    }

    http.Response.ContentType = "text/csv; charset=utf-8";
    http.Response.Headers["Content-Disposition"] =
        $"attachment; filename=\"audit-{DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv\"";
    http.Response.Headers["Cache-Control"] = "no-store";
    http.Response.Headers["X-Robots-Tag"] = "noindex";
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";

    var writer = new StreamWriter(
        http.Response.Body,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        bufferSize: 4096,
        leaveOpen: true);

    await using (writer)
    {
        await writer.WriteLineAsync(
            "created_at_utc,created_at_beijing,actor_type,actor_id,actor_handle,action,target_type,target_id,request_id,metadata_json");

        var rowCount = 0;
        try
        {
            await foreach (var row in auditAdmin.ExportAsync(filter, http.RequestAborted))
            {
                await writer.WriteLineAsync(Csv(
                    row.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    row.CreatedAt.ToOffset(TimeSpan.FromHours(8))
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    row.ActorType,
                    row.ActorId?.ToString(),
                    row.ActorHandle,
                    row.Action,
                    row.TargetType,
                    row.TargetId?.ToString(),
                    row.RequestId,
                    row.MetadataJson));
                rowCount++;
            }

            await writer.FlushAsync();
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Audit export cancelled for user {UserId}.", actorId);
            return;
        }

        try
        {
            await auditAdmin.RecordExportAsync(
                actorId,
                filter,
                rowCount,
                auditContextFactory.Create(actorId),
                CancellationToken.None);
        }
        catch (Exception exception) when (http.Response.HasStarted)
        {
            logger.LogError(exception, "Failed to record audit export for user {UserId}.", actorId);
        }

        logger.LogInformation("Audit CSV exported by user {UserId} with {Rows} rows.", actorId, rowCount);
    }
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/service-accounts/create", async (
    HttpContext http,
    IAntiforgery antiforgery,
    ServiceAccountAdminService serviceAccounts,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var ipAllowlist = form["ipAllowlist"].ToString()
        .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var result = await serviceAccounts.CreateAsync(
        form["name"].ToString(),
        form["scopes"].Select(value => value ?? string.Empty).ToArray(),
        ipAllowlist,
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return result.Outcome switch
    {
        ServiceAccountOutcome.Success => Results.Redirect($"/admin/service-accounts/{result.AccountId}"),
        ServiceAccountOutcome.DuplicateName => Results.Redirect("/admin/service-accounts?error=duplicate"),
        ServiceAccountOutcome.InvalidInput => Results.Redirect("/admin/service-accounts?error=invalid"),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/service-accounts/{accountId:guid}/disable", async (
    Guid accountId,
    HttpContext http,
    IAntiforgery antiforgery,
    ServiceAccountAdminService serviceAccounts,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    if (!form["confirmDisable"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Redirect($"/admin/service-accounts/{accountId}?error=confirm");
    }

    var outcome = await serviceAccounts.DisableAsync(
        accountId,
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return outcome switch
    {
        ServiceAccountOutcome.Success =>
            Results.Redirect($"/admin/service-accounts/{accountId}?message=disabled"),
        ServiceAccountOutcome.Unchanged =>
            Results.Redirect($"/admin/service-accounts/{accountId}?message=unchanged"),
        ServiceAccountOutcome.NotFound => Results.NotFound(),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/service-accounts/{accountId:guid}/tokens/issue", async (
    Guid accountId,
    HttpContext http,
    IAntiforgery antiforgery,
    ServiceAccountAdminService serviceAccounts,
    ServiceTokenRevealCache revealCache,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    if (!form["confirmIssue"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Redirect($"/admin/service-accounts/{accountId}?error=confirm");
    }

    if (!int.TryParse(form["days"].ToString(), out var days))
    {
        return Results.Redirect($"/admin/service-accounts/{accountId}?error=invalid");
    }

    var result = await serviceAccounts.IssueTokenAsync(
        accountId,
        days,
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return result.Outcome switch
    {
        ServiceAccountOutcome.Success => StoreServiceTokenReveal(revealCache, actorId, result),
        ServiceAccountOutcome.AccountDisabled =>
            Results.Redirect($"/admin/service-accounts/{accountId}?error=disabled"),
        ServiceAccountOutcome.InvalidInput =>
            Results.Redirect($"/admin/service-accounts/{accountId}?error=invalid"),
        ServiceAccountOutcome.NotFound => Results.NotFound(),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/service-accounts/{accountId:guid}/tokens/rotate", async (
    Guid accountId,
    HttpContext http,
    IAntiforgery antiforgery,
    ServiceAccountAdminService serviceAccounts,
    ServiceTokenRevealCache revealCache,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    if (!form["confirmRotate"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Redirect($"/admin/service-accounts/{accountId}?error=confirm");
    }

    if (!int.TryParse(form["days"].ToString(), out var days))
    {
        return Results.Redirect($"/admin/service-accounts/{accountId}?error=invalid");
    }

    var result = await serviceAccounts.RotateTokenAsync(
        accountId,
        days,
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return result.Outcome switch
    {
        ServiceAccountOutcome.Success => StoreServiceTokenReveal(revealCache, actorId, result),
        ServiceAccountOutcome.AccountDisabled =>
            Results.Redirect($"/admin/service-accounts/{accountId}?error=disabled"),
        ServiceAccountOutcome.InvalidInput =>
            Results.Redirect($"/admin/service-accounts/{accountId}?error=invalid"),
        ServiceAccountOutcome.NotFound => Results.NotFound(),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/service-accounts/{accountId:guid}/tokens/{tokenId:guid}/revoke", async (
    Guid accountId,
    Guid tokenId,
    HttpContext http,
    IAntiforgery antiforgery,
    ServiceAccountAdminService serviceAccounts,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    if (!form["confirmRevoke"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Redirect($"/admin/service-accounts/{accountId}?error=confirm");
    }

    var outcome = await serviceAccounts.RevokeTokenAsync(
        accountId,
        tokenId,
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return outcome switch
    {
        ServiceAccountOutcome.Success =>
            Results.Redirect($"/admin/service-accounts/{accountId}?message=revoked"),
        ServiceAccountOutcome.Unchanged =>
            Results.Redirect($"/admin/service-accounts/{accountId}?message=unchanged"),
        ServiceAccountOutcome.NotFound => Results.NotFound(),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/ai/update", async (
    HttpContext http,
    IAntiforgery antiforgery,
    AiSettingsService aiSettings,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    if (!int.TryParse(form["dailyTokenBudget"].ToString(), out var dailyTokenBudget))
    {
        return Results.Redirect("/admin/ai?error=budget");
    }

    if (!int.TryParse(form["dailyTaskBudget"].ToString(), out var dailyTaskBudget))
    {
        return Results.Redirect("/admin/ai?error=task-budget");
    }

    if (!int.TryParse(form["dailyContentBudget"].ToString(), out var dailyContentBudget))
    {
        return Results.Redirect("/admin/ai?error=content-budget");
    }

    if (!int.TryParse(form["qualityMinDecided"].ToString(), out var qualityMinDecided)
        || !decimal.TryParse(
            form["qualityMinAgreement"].ToString(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var qualityMinAgreement))
    {
        return Results.Redirect("/admin/ai?error=quality");
    }

    var models = new List<ApprovedModel>();
    foreach (var line in form["models"].ToString().Split('\n'))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            continue;
        }

        var parts = trimmed.Split('|');
        if (parts.Length != 3)
        {
            return Results.Redirect("/admin/ai?error=models");
        }

        models.Add(new ApprovedModel(parts[0], parts[1], parts[2]));
    }

    var result = await aiSettings.UpdateAsync(
        form["enabled"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase),
        form["suggestionsPaused"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase),
        models,
        dailyTokenBudget,
        dailyTaskBudget,
        dailyContentBudget,
        qualityMinDecided,
        qualityMinAgreement,
        form["confirmEnable"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase),
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return result.Outcome switch
    {
        AiSettingsOutcome.Success => Results.Redirect("/admin/ai?message=saved"),
        AiSettingsOutcome.Unchanged => Results.Redirect("/admin/ai?message=unchanged"),
        AiSettingsOutcome.ConfirmationRequired => Results.Redirect("/admin/ai?error=confirm"),
        AiSettingsOutcome.InvalidInput =>
            Results.Redirect($"/admin/ai?error={result.ErrorCode ?? "invalid"}"),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/moderation/words/create", async (
    HttpContext http,
    IAntiforgery antiforgery,
    SensitiveWordAdminService sensitiveWords,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var outcome = await sensitiveWords.CreateAsync(
        form["term"].ToString(),
        form["action"].ToString(),
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return outcome switch
    {
        SensitiveWordOutcome.Success => Results.Redirect("/admin/moderation/words?message=created"),
        SensitiveWordOutcome.DuplicateTerm => Results.Redirect("/admin/moderation/words?error=duplicate"),
        SensitiveWordOutcome.InvalidInput => Results.Redirect("/admin/moderation/words?error=invalid"),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/moderation/words/{wordId:guid}/update", async (
    Guid wordId,
    HttpContext http,
    IAntiforgery antiforgery,
    SensitiveWordAdminService sensitiveWords,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var outcome = await sensitiveWords.UpdateAsync(
        wordId,
        form["term"].ToString(),
        form["action"].ToString(),
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return outcome switch
    {
        SensitiveWordOutcome.Success => Results.Redirect("/admin/moderation/words?message=updated"),
        SensitiveWordOutcome.Unchanged => Results.Redirect("/admin/moderation/words?message=unchanged"),
        SensitiveWordOutcome.DuplicateTerm => Results.Redirect("/admin/moderation/words?error=duplicate"),
        SensitiveWordOutcome.InvalidInput => Results.Redirect("/admin/moderation/words?error=invalid"),
        SensitiveWordOutcome.NotFound => Results.NotFound(),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/moderation/words/{wordId:guid}/disable", async (
    Guid wordId,
    HttpContext http,
    IAntiforgery antiforgery,
    SensitiveWordAdminService sensitiveWords,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    if (!form["confirmDisable"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Redirect("/admin/moderation/words?error=confirm");
    }

    var outcome = await sensitiveWords.DisableAsync(
        wordId,
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return outcome switch
    {
        SensitiveWordOutcome.Success => Results.Redirect("/admin/moderation/words?message=disabled"),
        SensitiveWordOutcome.Unchanged => Results.Redirect("/admin/moderation/words?message=unchanged"),
        SensitiveWordOutcome.NotFound => Results.NotFound(),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/moderation/words/policy", async (
    HttpContext http,
    IAntiforgery antiforgery,
    SensitiveWordAdminService sensitiveWords,
    AuditRequestContextFactory auditContextFactory,
    ILogger<Program> logger) =>
{
    var failure = await ValidateFormAsync(http, antiforgery, logger);
    if (failure is not null)
    {
        return failure;
    }

    if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
    {
        return Results.Forbid();
    }

    var form = await http.Request.ReadFormAsync(http.RequestAborted);
    var outcome = await sensitiveWords.UpdatePolicyAsync(
        form["hit"].ToString(),
        auditContextFactory.Create(actorId),
        http.RequestAborted);

    return outcome switch
    {
        SensitiveWordOutcome.Success => Results.Redirect("/admin/moderation/words?message=policy"),
        SensitiveWordOutcome.Unchanged => Results.Redirect("/admin/moderation/words?message=unchanged"),
        SensitiveWordOutcome.InvalidInput => Results.Redirect("/admin/moderation/words?error=invalid"),
        _ => Results.Forbid()
    };
}).RequireAuthorization("AdminOnly");

app.MapAdminConsoleEndpoints();
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

static string MapDeviceError(ApiProblemException exception)
{
    return exception.Code switch
    {
        ApiErrorCodes.ChallengeNotFound => "challenge-not-found",
        ApiErrorCodes.ChallengeExpired => "challenge-expired",
        ApiErrorCodes.ChallengeUsed => "challenge-used",
        ApiErrorCodes.SignatureInvalid => "signature-invalid",
        ApiErrorCodes.TimestampInvalid => "timestamp-invalid",
        ApiErrorCodes.PlatformUnsupported => "platform-unsupported",
        ApiErrorCodes.DeviceAlreadyBound => "device-bound",
        ApiErrorCodes.DeviceLimitReached => "device-limit",
        ApiErrorCodes.DeviceVerificationLocked => "locked",
        ApiErrorCodes.Forbidden => "forbidden",
        ApiErrorCodes.AuthRequired => "auth",
        ApiErrorCodes.NotFound => "not-found",
        _ => "invalid"
    };
}

static string MapCommentError(ApiProblemException exception)
{
    if (exception.Code == ApiErrorCodes.AccountMuted)
    {
        return "muted";
    }

    if (exception.Code == ApiErrorCodes.Forbidden
        && DetailReason(exception) == "activation_required")
    {
        return "activation";
    }

    return "invalid";
}

static string RenderRss(
    IReadOnlyList<RssItemView> items,
    DateTimeOffset lastModified,
    PublicSiteLinks siteLinks)
{
    XNamespace atom = "http://www.w3.org/2005/Atom";
    XNamespace dc = "http://purl.org/dc/elements/1.1/";
    var channel = new XElement(
        "channel",
        new XElement("title", "WhalePod（鲸群）"),
        new XElement("link", siteLinks.BaseUrl),
        new XElement("description", "可复现的 AI 工具实测记录与可直接复制的产物。"),
        new XElement("language", "zh-cn"),
        lastModified > DateTimeOffset.UnixEpoch
            ? new XElement("lastBuildDate", lastModified.UtcDateTime.ToString("R"))
            : null,
        new XElement(
            atom + "link",
            new XAttribute("href", siteLinks.Absolute("/rss.xml")),
            new XAttribute("rel", "self"),
            new XAttribute("type", "application/rss+xml")),
        items.Select(item =>
        {
            var link = siteLinks.Absolute($"/posts/{item.Id}");
            return new XElement(
                "item",
                new XElement("title", item.Title),
                new XElement("link", link),
                new XElement("guid", new XAttribute("isPermaLink", "true"), link),
                new XElement("pubDate", item.CreatedAt.UtcDateTime.ToString("R")),
                new XElement("description", item.Excerpt),
                new XElement("category", item.CategorySlug),
                new XElement(dc + "creator", $"@{item.AuthorHandle}"));
        }));
    var document = new XDocument(
        new XDeclaration("1.0", "utf-8", null),
        new XElement(
            "rss",
            new XAttribute("version", "2.0"),
            new XAttribute(XNamespace.Xmlns + "atom", atom),
            new XAttribute(XNamespace.Xmlns + "dc", dc),
            channel));
    return document.ToString(SaveOptions.DisableFormatting);
}

static bool TryWriteNotModified(
    HttpContext http,
    DateTimeOffset lastModified,
    string resourceName)
{
    var etag = $"\"{resourceName}-{lastModified.UtcTicks}\"";
    http.Response.Headers.CacheControl = "public, max-age=300";
    http.Response.Headers.ETag = etag;
    http.Response.Headers.LastModified = lastModified.ToUniversalTime().ToString("R");
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";

    var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
    if (!string.IsNullOrWhiteSpace(ifNoneMatch))
    {
        var matches = ifNoneMatch
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => value == "*" || value == etag || value == $"W/{etag}");
        if (matches)
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
        }

        return matches;
    }

    var ifModifiedSince = http.Request.GetTypedHeaders().IfModifiedSince;
    if (lastModified > DateTimeOffset.UnixEpoch
        && ifModifiedSince is not null
        && lastModified.ToUnixTimeSeconds() <= ifModifiedSince.Value.ToUnixTimeSeconds())
    {
        http.Response.StatusCode = StatusCodes.Status304NotModified;
        return true;
    }

    return false;
}

static string RenderSitemapIndex(
    IReadOnlyList<SitemapShard> shards,
    PublicSiteLinks siteLinks)
{
    XNamespace sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";
    var document = new XDocument(
        new XDeclaration("1.0", "utf-8", null),
        new XElement(
            sitemap + "sitemapindex",
            shards
                .GroupBy(shard => shard.Path, StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(shard => new XElement(
                    sitemap + "sitemap",
                    new XElement(sitemap + "loc", siteLinks.Absolute(shard.Path)),
                    new XElement(
                        sitemap + "lastmod",
                        shard.LastModified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))))));
    return document.ToString(SaveOptions.DisableFormatting);
}

static string RenderSitemap(
    IReadOnlyList<SitemapEntry> entries,
    PublicSiteLinks siteLinks)
{
    XNamespace sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";
    var document = new XDocument(
        new XDeclaration("1.0", "utf-8", null),
        new XElement(
            sitemap + "urlset",
            entries
                .GroupBy(entry => entry.Path, StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(entry => new XElement(
                    sitemap + "url",
                    new XElement(sitemap + "loc", siteLinks.Absolute(entry.Path)),
                    new XElement(
                        sitemap + "lastmod",
                        entry.LastModified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))))));
    return document.ToString(SaveOptions.DisableFormatting);
}

static string MapAccountDeletionError(ApiProblemException exception)
{
    if (exception.Code == ApiErrorCodes.Forbidden)
    {
        return DetailReason(exception) == "last_admin" ? "last-admin" : "invalid";
    }

    return exception.Code switch
    {
        ApiErrorCodes.Conflict => "conflict",
        ApiErrorCodes.ValidationFailed => "confirm",
        _ => "invalid"
    };
}

static string MapAppealSubmitError(ApiProblemException exception)
{
    return exception.Code switch
    {
        ApiErrorCodes.Forbidden => "forbidden",
        ApiErrorCodes.NotFound => "target",
        ApiErrorCodes.Conflict => "conflict",
        ApiErrorCodes.ValidationFailed => DetailField(exception) == "reason" ? "reason" : "target",
        _ => "invalid"
    };
}

static string MapAppealReviewError(ApiProblemException exception)
{
    return exception.Code switch
    {
        ApiErrorCodes.Forbidden => "self",
        ApiErrorCodes.NotFound => "missing",
        ApiErrorCodes.Conflict => "conflict",
        ApiErrorCodes.ValidationFailed => "outcome",
        _ => "invalid"
    };
}

static string? DetailReason(ApiProblemException exception)
{
    if (exception.Details is null)
    {
        return null;
    }

    var details = JsonSerializer.SerializeToElement(exception.Details);
    return details.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
}

static string? DetailField(ApiProblemException exception)
{
    if (exception.Details is null)
    {
        return null;
    }

    var details = JsonSerializer.SerializeToElement(exception.Details);
    return details.TryGetProperty("issues", out var issues)
        && issues.ValueKind == JsonValueKind.Array
        && issues.GetArrayLength() > 0
        && issues[0].TryGetProperty("field", out var field)
        ? field.GetString()
        : null;
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

static IResult UnsubscribeResult(int statusCode, string title, string message)
{
    var encodedTitle = WebUtility.HtmlEncode(title);
    var encodedMessage = WebUtility.HtmlEncode(message);
    return Results.Text(
        $"""
        <!doctype html>
        <html lang="zh-CN">
        <head><meta charset="utf-8" /><meta name="robots" content="noindex" /><title>{encodedTitle} - WhalePod</title></head>
        <body>
        <main>
        <h1>{encodedTitle}</h1>
        <p>{encodedMessage}</p>
        <p><a href="/settings/emails">管理邮件偏好</a> · <a href="/">返回首页</a></p>
        </main>
        </body>
        </html>
        """,
        "text/html; charset=utf-8",
        statusCode: statusCode);
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

static string Csv(params string?[] values)
{
    return string.Join(",", values.Select(value => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\""));
}

static IResult StoreServiceTokenReveal(
    ServiceTokenRevealCache revealCache,
    Guid actorId,
    ServiceTokenCreateResult result)
{
    if (result.Token is null)
    {
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }

    revealCache.Store(actorId, result.Token);
    return Results.Redirect("/admin/service-accounts/tokens/created");
}

static IResult StoreInviteReveal(
    InviteRevealCache revealCache,
    Guid actorId,
    InviteCreateResult result)
{
    if (result.Invite is null)
    {
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }

    revealCache.Store(actorId, result.Invite);
    return Results.Redirect("/admin/invites/created");
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