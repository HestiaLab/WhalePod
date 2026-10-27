using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Interactions;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class AdminConsoleHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Admin_console_pages_csrf_and_actions_are_enforced()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("console-admin", UserRoles.Admin);
        var member = ServiceTestSupport.NewUser("console-member");
        var target = ServiceTestSupport.NewUser("console-target");
        var category = ServiceTestSupport.NewCategory("管理后台测试板块");
        admin.Handle = $"console-admin-{suffix}";
        member.Handle = $"console-member-{suffix}";
        target.Handle = $"console-target-{suffix}";
        var pendingPost = ServiceTestSupport.NewPost(
            target.Id,
            category.Id,
            ModerationStatuses.Pending,
            title: $"管理后台待审帖子 {suffix}");
        var approvedPost = ServiceTestSupport.NewPost(
            target.Id,
            category.Id,
            ModerationStatuses.Approved,
            title: $"管理后台举报目标帖子 {suffix}");
        db.Users.AddRange(admin, member, target);
        db.Categories.Add(category);
        db.Posts.AddRange(pendingPost, approvedPost);
        var report = new Report
        {
            ReporterId = member.Id,
            TargetType = ReportTargetTypes.Post,
            TargetId = approvedPost.Id,
            Reason = ReportReasons.Spam,
            Detail = "管理后台举报处理测试",
            Status = ReportStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync();

        using var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var adminSession = await environment.LoginAsync(admin.Handle);
        var memberSession = await environment.LoginAsync(member.Handle);

        var adminPages = new[]
        {
            "/admin",
            "/admin/moderation",
            $"/admin/moderation/post/{pendingPost.Id}",
            "/admin/reports",
            "/admin/users",
            $"/admin/users/{target.Id}",
            "/admin/categories",
            "/admin/mode"
        };
        foreach (var path in adminPages)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", adminSession);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            Assert.Contains("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        }

        foreach (var path in new[]
                 {
                     "/admin/moderation",
                     "/admin/reports",
                     "/admin/users",
                     "/admin/categories",
                     "/admin/mode"
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", memberSession);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var content = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            Assert.Contains("没有访问权限", content, StringComparison.Ordinal);
        }

        using (var anonymous = await client.GetAsync("/admin/moderation"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Contains(
                "/login?ReturnUrl=",
                anonymous.Headers.Location?.OriginalString,
                StringComparison.Ordinal);
        }

        using (var missingCsrf = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/admin/moderation/post/{pendingPost.Id}/action",
                   new Dictionary<string, string>
                   {
                       ["action"] = "approve",
                       ["expectedContentVersion"] = pendingPost.Version.ToString()
                   },
                   adminSession,
                   string.Empty))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        }

        var detailForm = await WebFormTestSupport.LoadFormAsync(
            client,
            $"/admin/moderation/post/{pendingPost.Id}",
            adminSession);
        using (var approve = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/admin/moderation/post/{pendingPost.Id}/action",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = detailForm.Token,
                       ["action"] = "approve",
                       ["expectedContentVersion"] = pendingPost.Version.ToString()
                   },
                   adminSession,
                   detailForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
        }

        db.ChangeTracker.Clear();
        Assert.Equal(
            ModerationStatuses.Approved,
            (await db.Posts.SingleAsync(post => post.Id == pendingPost.Id)).ModerationStatus);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "moderation.approve"
            && log.TargetId == pendingPost.Id
            && log.ActorId == admin.Id));

        var reportForm = await WebFormTestSupport.LoadFormAsync(client, "/admin/reports", adminSession);
        using (var dismiss = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/admin/reports/{report.Id}/resolve",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = reportForm.Token,
                       ["outcome"] = "dismissed",
                       ["resolutionNote"] = "举报不成立，测试驳回",
                       ["actionType"] = string.Empty,
                       ["actionReason"] = string.Empty,
                       ["actionExpiresAt"] = string.Empty
                   },
                   adminSession,
                   reportForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, dismiss.StatusCode);
        }

        db.ChangeTracker.Clear();
        var storedReport = await db.Reports.SingleAsync(candidate => candidate.Id == report.Id);
        Assert.Equal(ReportStatuses.Dismissed, storedReport.Status);
        Assert.Equal(admin.Id, storedReport.HandledBy);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "report.dismissed" && log.TargetId == report.Id));

        var userForm = await WebFormTestSupport.LoadFormAsync(
            client,
            $"/admin/users/{target.Id}",
            adminSession);
        using (var mute = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/admin/users/{target.Id}/status",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = userForm.Token,
                       ["status"] = "muted",
                       ["reason"] = "管理后台禁言测试",
                       ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm")
                   },
                   adminSession,
                   userForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, mute.StatusCode);
        }

        db.ChangeTracker.Clear();
        Assert.Equal(
            UserStatuses.Muted,
            (await db.Users.SingleAsync(user => user.Id == target.Id)).Status);

        var categoryForm = await WebFormTestSupport.LoadFormAsync(
            client,
            "/admin/categories",
            adminSession);
        var newSlug = $"console-cat-{suffix}";
        using (var createCategory = await WebFormTestSupport.PostFormAsync(
                   client,
                   "/admin/categories/create",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = categoryForm.Token,
                       ["slug"] = newSlug,
                       ["name"] = "管理后台创建板块",
                       ["description"] = "分类管理 HTTP 测试",
                       ["displayOrder"] = "99"
                   },
                   adminSession,
                   categoryForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, createCategory.StatusCode);
        }

        db.ChangeTracker.Clear();
        Assert.True(await db.Categories.AnyAsync(item => item.Slug == newSlug));
    }
}