using System.Text.Json;

using WhalePod.Domain;
using WhalePod.Domain.Interactions;

namespace WhalePod.Infrastructure.Services;

internal static class NotificationPresentation
{
    private static readonly string[] TargetTypes = ["post", "comment", "user", "device", "account"];

    public static NotificationPayloadView Map(Notification notification)
    {
        var root = notification.PayloadJson.RootElement;
        var isObject = root.ValueKind == JsonValueKind.Object;
        var action = isObject ? TryString(root, "action") : null;
        var summary = NormalizeSummary(isObject ? TryString(root, "summary") : null)
            ?? DefaultSummary(notification.Type, action);
        var targetType = NormalizeTargetType(isObject ? TryString(root, "targetType") : null)
            ?? DefaultTargetType(notification.Type);
        var targetId = isObject
            ? TryGuid(root, "targetId")
                ?? (notification.Type is NotificationTypes.Reply or NotificationTypes.CommentAccepted
                    ? TryGuid(root, "commentId")
                    : null)
            : null;
        var targetUrl = NormalizeTargetUrl(isObject ? TryString(root, "targetUrl") : null)
            ?? BuildFallbackUrl(notification.Type, targetType, targetId, root, isObject);
        return new NotificationPayloadView(summary, targetType, targetId, targetUrl);
    }

    public static string DefaultSummary(string type, string? action)
    {
        return type switch
        {
            NotificationTypes.Reply => "有人回复了你的评论。",
            NotificationTypes.CommentAccepted => action == "unaccepted"
                ? "你的评论已被取消采纳。"
                : "你的评论被采纳为答案。",
            NotificationTypes.Mention => "有人在内容中提到了你。",
            NotificationTypes.PostApproved => "你的帖子已通过审核。",
            NotificationTypes.PostRejected => "你的帖子未通过审核。",
            NotificationTypes.CommentApproved => "你的评论已通过审核。",
            NotificationTypes.CommentRejected => "你的评论未通过审核。",
            NotificationTypes.DeviceVerified => "你的设备已通过验证。",
            NotificationTypes.DeviceRevoked => "你的设备已被撤销。",
            NotificationTypes.AccountStatusChanged => "你的账号状态已更新。",
            NotificationTypes.ReportResult => "你的举报已有处理结果。",
            NotificationTypes.AppealResult => "你的申诉已有处理结果。",
            _ => "你有一条新通知。"
        };
    }

    private static string? DefaultTargetType(string type)
    {
        return type switch
        {
            NotificationTypes.Reply or NotificationTypes.CommentAccepted => "comment",
            NotificationTypes.PostApproved or NotificationTypes.PostRejected => "post",
            NotificationTypes.CommentApproved or NotificationTypes.CommentRejected => "comment",
            NotificationTypes.DeviceVerified or NotificationTypes.DeviceRevoked => "device",
            NotificationTypes.AccountStatusChanged => "account",
            _ => null
        };
    }

    private static string? NormalizeTargetType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return TargetTypes.Contains(normalized, StringComparer.Ordinal) ? normalized : null;
    }

    private static string? NormalizeSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = string.Concat(value.Where(character => !char.IsControl(character))).Trim();
        return RuneHelper.Truncate(cleaned, 300);
    }

    private static string? NormalizeTargetUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > 512
            || !normalized.StartsWith('/')
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || normalized.Contains('\\')
            || normalized.Any(char.IsControl)
            || !Uri.TryCreate(normalized, UriKind.Relative, out _))
        {
            return null;
        }

        return normalized;
    }

    private static string? BuildFallbackUrl(
        string type,
        string? targetType,
        Guid? targetId,
        JsonElement root,
        bool isObject)
    {
        if (targetId is null)
        {
            return null;
        }

        if (targetType == "post")
        {
            return $"/posts/{targetId}";
        }

        if ((type is NotificationTypes.Reply
                or NotificationTypes.CommentAccepted
                or NotificationTypes.CommentApproved
                or NotificationTypes.CommentRejected)
            && isObject)
        {
            var postId = TryGuid(root, "postId");
            if (postId is not null)
            {
                return $"/posts/{postId}?comment={targetId}";
            }
        }

        return null;
    }

    private static string? TryString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static Guid? TryGuid(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out var value)
            ? value
            : null;
    }
}