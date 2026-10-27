
namespace WhalePod.Infrastructure.Services;

public static class ApiErrorCodes
{
    public const string AuthRequired = "AUTH_REQUIRED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Conflict = "CONFLICT";
    public const string AccountMuted = "ACCOUNT_MUTED";
    public const string AccountBanned = "ACCOUNT_BANNED";
    public const string ContentVersionStale = "CONTENT_VERSION_STALE";
    public const string RateLimited = "RATE_LIMITED";
    public const string UploadTooLarge = "UPLOAD_TOO_LARGE";
    public const string UploadUnsupportedType = "UPLOAD_UNSUPPORTED_TYPE";
    public const string ChallengeNotFound = "CHALLENGE_NOT_FOUND";
    public const string ChallengeExpired = "CHALLENGE_EXPIRED";
    public const string ChallengeUsed = "CHALLENGE_USED";
    public const string SignatureInvalid = "SIGNATURE_INVALID";
    public const string TimestampInvalid = "TIMESTAMP_INVALID";
    public const string PlatformUnsupported = "PLATFORM_UNSUPPORTED";
    public const string DeviceAlreadyBound = "DEVICE_ALREADY_BOUND";
    public const string DeviceLimitReached = "DEVICE_LIMIT_REACHED";
    public const string DeviceVerificationLocked = "DEVICE_VERIFICATION_LOCKED";
    public const string ServiceTokenInvalid = "SERVICE_TOKEN_INVALID";
    public const string ServiceTokenExpired = "SERVICE_TOKEN_EXPIRED";
    public const string ServiceTokenRevoked = "SERVICE_TOKEN_REVOKED";
    public const string ScopeDenied = "SCOPE_DENIED";
    public const string AgentDisabled = "AGENT_DISABLED";
    public const string AgentRateLimited = "AGENT_RATE_LIMITED";
    public const string TaskNotFound = "TASK_NOT_FOUND";
    public const string TaskAlreadyClaimed = "TASK_ALREADY_CLAIMED";
    public const string TaskLeaseExpired = "TASK_LEASE_EXPIRED";
    public const string TaskAlreadyDecided = "TASK_ALREADY_DECIDED";
    public const string SuggestionInvalid = "SUGGESTION_INVALID";
}

public sealed class ApiProblemException(
    string code,
    int statusCode,
    string message,
    object? details = null,
    int? retryAfterSeconds = null) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public object? Details { get; } = details;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;

    public static ApiProblemException Validation(string field, string message)
    {
        return new ApiProblemException(
            ApiErrorCodes.ValidationFailed,
            400,
            "请求字段校验失败。",
            new { issues = new[] { new { field, message } } });
    }

    public static ApiProblemException Validation(IEnumerable<(string Field, string Message)> issues)
    {
        var materialized = issues
            .Select(issue => new { field = issue.Field, message = issue.Message })
            .ToArray();
        return new ApiProblemException(
            ApiErrorCodes.ValidationFailed,
            400,
            "请求字段校验失败。",
            new { issues = materialized });
    }

    public static ApiProblemException NotFound(string message = "请求的内容不存在或当前不可见。")
    {
        return new ApiProblemException(
            ApiErrorCodes.NotFound,
            404,
            message);
    }

    public static ApiProblemException Forbidden(string? reason = null, string message = "没有权限执行此操作。")
    {
        return new ApiProblemException(
            ApiErrorCodes.Forbidden,
            403,
            message,
            reason is null ? null : new { reason });
    }

    public static ApiProblemException Conflict(string message = "当前状态不允许此操作。", object? details = null)
    {
        return new ApiProblemException(
            ApiErrorCodes.Conflict,
            409,
            message,
            details);
    }

    public static ApiProblemException UploadTooLarge(long maxBytes)
    {
        return new ApiProblemException(
            ApiErrorCodes.UploadTooLarge,
            413,
            "图片不能超过 10 MB。",
            new { field = "file", maxBytes });
    }

    public static ApiProblemException UploadUnsupportedType()
    {
        return new ApiProblemException(
            ApiErrorCodes.UploadUnsupportedType,
            415,
            "仅支持 JPG、PNG、WebP、GIF 图片，不允许 SVG 或可执行文件。",
            new { field = "file" });
    }

    public static ApiProblemException DeviceLocked(int retryAfterSeconds)
    {
        return new ApiProblemException(
            ApiErrorCodes.DeviceVerificationLocked,
            429,
            "连续验证失败次数过多，请按 Retry-After 等待后重试。",
            new { retryAfterSeconds },
            retryAfterSeconds);
    }
}