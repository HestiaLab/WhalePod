using WhalePod.Domain;

namespace WhalePod.Infrastructure.Email;

public static class EmailProviders
{
    public const string Smtp = "Smtp";
    public const string Logging = "Logging";
}

public sealed class EmailOptions
{
    public string Provider { get; set; } = string.Empty;
    public string PublicBaseUrl { get; set; } = "http://localhost:5224";
    public string SiteName { get; set; } = "WhalePod（鲸群）";
    public string? SuppressionHashSecret { get; set; }
    public string? UnsubscribeSecret { get; set; }
    public int UnsubscribeTokenLifetimeDays { get; set; } = 30;
    public int PollIntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 300;
    public int MaxAttempts { get; set; } = 5;
    public int BackoffBaseSeconds { get; set; } = 60;
    public int BackoffMaxSeconds { get; set; } = 3600;
    public SmtpEmailOptions Smtp { get; set; } = new();
}

public sealed class SmtpEmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "WhalePod";
    public int TimeoutSeconds { get; set; } = 30;
}

public static class EmailPreferencePolicy
{
    public static string? ForNotificationType(string notificationType)
    {
        return notificationType switch
        {
            NotificationTypes.Reply => EmailPreferenceTypes.RepliesMentions,
            NotificationTypes.Mention => EmailPreferenceTypes.RepliesMentions,
            NotificationTypes.CommentAccepted => EmailPreferenceTypes.RepliesMentions,
            NotificationTypes.PostApproved => EmailPreferenceTypes.ModerationResults,
            NotificationTypes.PostRejected => EmailPreferenceTypes.ModerationResults,
            NotificationTypes.CommentApproved => EmailPreferenceTypes.ModerationResults,
            NotificationTypes.CommentRejected => EmailPreferenceTypes.ModerationResults,
            NotificationTypes.ReportResult => EmailPreferenceTypes.ModerationResults,
            NotificationTypes.AppealResult => EmailPreferenceTypes.ModerationResults,
            NotificationTypes.AccountStatusChanged => EmailPreferenceTypes.AccountSecurity,
            NotificationTypes.DeviceVerified => EmailPreferenceTypes.AccountSecurity,
            NotificationTypes.DeviceRevoked => EmailPreferenceTypes.AccountSecurity,
            "account_deleted" => EmailPreferenceTypes.AccountSecurity,
            _ => null
        };
    }

    public static bool IsSecurity(string notificationType)
    {
        return ForNotificationType(notificationType) == EmailPreferenceTypes.AccountSecurity;
    }

    public static bool IsUnsubscribable(string preferenceType)
    {
        return preferenceType is EmailPreferenceTypes.RepliesMentions
            or EmailPreferenceTypes.ModerationResults;
    }
}