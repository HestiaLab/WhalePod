namespace WhalePod.Domain;

public static class UserStatuses
{
    public const string Active = "active";
    public const string Muted = "muted";
    public const string Banned = "banned";
    public const string Deleted = "deleted";
}

public static class UserRoles
{
    public const string Member = "member";
    public const string Admin = "admin";
}

public static class InviteCodeStatuses
{
    public const string Active = "active";
    public const string Disabled = "disabled";
}

public static class DeviceStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}

public static class Platforms
{
    public const string Win32X64 = "win32-x64";
    public const string Win32Arm64 = "win32-arm64";
    public const string DarwinX64 = "darwin-x64";
    public const string DarwinArm64 = "darwin-arm64";
    public const string LinuxX64 = "linux-x64";
    public const string LinuxArm64 = "linux-arm64";
}

public static class ModerationStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Hidden = "hidden";
    public const string Deleted = "deleted";
}

public static class CategoryStatuses
{
    public const string Active = "active";
    public const string Disabled = "disabled";
}

public static class TagStatuses
{
    public const string Active = "active";
    public const string Hidden = "hidden";
}

public static class MediaUploadStatuses
{
    public const string Temp = "temp";
    public const string Attached = "attached";
    public const string Avatar = "avatar";
    public const string Deleted = "deleted";
}

public static class ArtifactKinds
{
    public const string Patch = "patch";
    public const string Yaml = "yaml";
    public const string Json = "json";
    public const string Bundle = "bundle";
    public const string Markdown = "markdown";
    public const string Shell = "shell";
}

public static class ReportTargetTypes
{
    public const string Post = "post";
    public const string Comment = "comment";
    public const string User = "user";
}

public static class ReportReasons
{
    public const string Spam = "spam";
    public const string Harassment = "harassment";
    public const string Misclassified = "misclassified";
    public const string Plagiarism = "plagiarism";
    public const string Other = "other";
}

public static class ReportStatuses
{
    public const string Pending = "pending";
    public const string Resolved = "resolved";
    public const string Dismissed = "dismissed";
}

public static class ModerationTargetTypes
{
    public const string Post = "post";
    public const string Comment = "comment";
}

public static class ModerationActorTypes
{
    public const string User = "user";
    public const string System = "system";
}

public static class ModerationActions
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Hide = "hide";
    public const string Restore = "restore";
    public const string Delete = "delete";
    public const string Escalate = "escalate";
    public const string Mute = "mute";
    public const string Ban = "ban";
}

public static class NotificationTypes
{
    public const string Reply = "reply";
    public const string CommentAccepted = "comment_accepted";
    public const string Mention = "mention";
    public const string PostApproved = "post_approved";
    public const string PostRejected = "post_rejected";
    public const string CommentApproved = "comment_approved";
    public const string CommentRejected = "comment_rejected";
    public const string DeviceVerified = "device_verified";
    public const string DeviceRevoked = "device_revoked";
    public const string AccountStatusChanged = "account_status_changed";
    public const string ReportResult = "report_result";
    public const string AppealResult = "appeal_result";
}

public static class EmailStatuses
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Suppressed = "suppressed";
}

public static class EmailPreferenceTypes
{
    public const string RepliesMentions = "replies_mentions";
    public const string ModerationResults = "moderation_results";
    public const string AccountSecurity = "account_security";
}

public static class EmailSuppressionReasons
{
    public const string HardBounce = "hard_bounce";
    public const string Complaint = "complaint";
    public const string Manual = "manual";
}

public static class ServiceAccountStatuses
{
    public const string Active = "active";
    public const string Disabled = "disabled";
}

public static class AgentInstanceStatuses
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Revoked = "revoked";
}

public static class RuntimeLocations
{
    public const string Local = "local";
}

public static class ModerationTaskStatuses
{
    public const string Queued = "queued";
    public const string Claimed = "claimed";
    public const string Suggested = "suggested";
    public const string HumanDecided = "human_decided";
    public const string Escalated = "escalated";
    public const string Expired = "expired";
}

public static class SuggestionTypes
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Escalate = "escalate";
}

public static class HumanDecisions
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Hide = "hide";
    public const string Delete = "delete";
    public const string Escalate = "escalate";
}

public static class AppealStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

public static class AppealTargetTypes
{
    public const string Post = "post";
    public const string Comment = "comment";
    public const string AccountPenalty = "account_penalty";
}

public static class ModerationCheckResults
{
    public const string Pass = "pass";
    public const string Review = "review";
    public const string Reject = "reject";
    public const string Error = "error";
}

public static class ModerationCheckTypes
{
    public const string Text = "text";
    public const string Image = "image";
}

public static class SensitiveWordActions
{
    public const string Manual = "manual";
    public const string Reject = "reject";
}

public static class SensitiveWordStatuses
{
    public const string Active = "active";
    public const string Disabled = "disabled";
}

public static class AuditActorTypes
{
    public const string User = "user";
    public const string Service = "service";
    public const string System = "system";
}