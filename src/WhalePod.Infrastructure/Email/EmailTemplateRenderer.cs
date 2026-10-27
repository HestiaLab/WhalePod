using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.Extensions.Options;

using WhalePod.Domain.Communications;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Infrastructure.Email;

public sealed record EmailTemplateContent(string Subject, string TextBody, string HtmlBody);

public sealed class EmailTemplateRenderer(
    IOptions<EmailOptions> options,
    EmailUnsubscribeTokenService unsubscribeTokens)
{
    private readonly EmailOptions _options = options.Value;

    public EmailTemplateContent Render(EmailOutbox email)
    {
        var payload = email.PayloadJson.RootElement;
        var action = TryGetString(payload, "action");
        var summary = TryGetString(payload, "summary")
            ?? NotificationPresentation.DefaultSummary(email.Template, action);
        var preferenceType = EmailPreferencePolicy.ForNotificationType(email.Template)
            ?? throw new InvalidOperationException("Email template does not map to a preference type.");
        var targetUrl = ToAbsoluteUrl(TryGetString(payload, "targetUrl"));
        var preferencesUrl = BuildUrl("/settings/emails");
        var title = SubjectFor(email.Template, action);
        var preferenceLine = $"管理邮件偏好：{preferencesUrl}";
        string? unsubscribeUrl = null;
        if (EmailPreferencePolicy.IsUnsubscribable(preferenceType))
        {
            var token = unsubscribeTokens.Create(email.UserId ?? throw new InvalidOperationException(
                "Unsubscribable email must have a recipient user."), preferenceType);
            unsubscribeUrl = BuildUrl($"/email/unsubscribe?token={Uri.EscapeDataString(token)}");
        }

        var text = new StringBuilder();
        text.AppendLine("你好，");
        text.AppendLine();
        text.AppendLine(summary);
        if (targetUrl is not null)
        {
            text.AppendLine();
            text.AppendLine($"查看详情：{targetUrl}");
        }

        text.AppendLine();
        text.AppendLine($"来自 {_options.SiteName}");
        if (email.Template != "account_deleted")
        {
            text.AppendLine(preferenceLine);
        }

        if (unsubscribeUrl is not null)
        {
            text.AppendLine($"退订此类邮件：{unsubscribeUrl}");
        }
        else if (EmailPreferencePolicy.IsSecurity(email.Template))
        {
            text.AppendLine("此邮件为账号安全通知，无法退订。");
        }

        var encodedSummary = HtmlEncoder.Default.Encode(summary);
        var html = new StringBuilder();
        html.AppendLine("<p>你好，</p>");
        html.AppendLine($"<p>{encodedSummary}</p>");
        if (targetUrl is not null)
        {
            html.AppendLine($"<p><a href=\"{HtmlEncoder.Default.Encode(targetUrl)}\">查看详情</a></p>");
        }

        html.AppendLine($"<hr /><p>来自 {HtmlEncoder.Default.Encode(_options.SiteName)}</p>");
        if (email.Template != "account_deleted")
        {
            html.AppendLine(
                $"<p><a href=\"{HtmlEncoder.Default.Encode(preferencesUrl)}\">管理邮件偏好</a></p>");
        }

        if (unsubscribeUrl is not null)
        {
            html.AppendLine(
                $"<p><a href=\"{HtmlEncoder.Default.Encode(unsubscribeUrl)}\">退订此类邮件</a></p>");
        }
        else if (EmailPreferencePolicy.IsSecurity(email.Template))
        {
            html.AppendLine("<p>此邮件为账号安全通知，无法退订。</p>");
        }

        return new EmailTemplateContent(title, text.ToString(), html.ToString());
    }

    private string? ToAbsoluteUrl(string? relativeUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl) || !relativeUrl.StartsWith('/'))
        {
            return null;
        }

        return BuildUrl(relativeUrl);
    }

    private string BuildUrl(string path)
    {
        return $"{_options.PublicBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
    }

    private static string SubjectFor(string template, string? action)
    {
        return template switch
        {
            "reply" => "有人回复了你的评论",
            "comment_accepted" => action == "unaccepted" ? "你的评论已被取消采纳" : "你的评论被采纳为答案",
            "mention" => "有人在内容中提到了你",
            "post_approved" => "你的帖子已通过审核",
            "post_rejected" => "你的帖子未通过审核",
            "comment_approved" => "你的评论已通过审核",
            "comment_rejected" => "你的评论未通过审核",
            "device_verified" => "设备验证成功",
            "device_revoked" => "设备已被撤销",
            "account_status_changed" => "账号状态已更新",
            "report_result" => "举报处理结果",
            "appeal_result" => "申诉处理结果",
            "account_deleted" => "账号注销完成",
            _ => "站点通知"
        };
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }
}