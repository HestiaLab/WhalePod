using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Domain.Moderation;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class InteractionWebFormHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Post_like_toggle_requires_csrf_is_idempotent_and_rejects_self_like()
    {
        var seed = await SeedAsync();
        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var cookie = await environment.LoginAsync(seed.Member.Handle);
        var formContext = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{seed.Post.Id}", cookie);

        using (var missingToken = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{seed.Post.Id}/like",
                   new Dictionary<string, string> { ["liked"] = "true" },
                   cookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        }

        using (var anonymous = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{seed.Post.Id}/like",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = formContext.Token,
                       ["liked"] = "true"
                   },
                   string.Empty,
                   string.Empty))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Contains("/login", anonymous.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        Assert.Equal(0, await CountPostLikesAsync(seed.Post.Id));

        using (var liked = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{seed.Post.Id}/like",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = formContext.Token,
                       ["liked"] = "true"
                   },
                   cookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, liked.StatusCode);
            Assert.Contains("message=liked", liked.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        Assert.Equal(1, await CountPostLikesAsync(seed.Post.Id));

        using (var likedAgain = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{seed.Post.Id}/like",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = formContext.Token,
                       ["liked"] = "true"
                   },
                   cookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, likedAgain.StatusCode);
        }

        Assert.Equal(1, await CountPostLikesAsync(seed.Post.Id));

        using (var unliked = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{seed.Post.Id}/like",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = formContext.Token,
                       ["liked"] = "false"
                   },
                   cookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, unliked.StatusCode);
        }

        Assert.Equal(0, await CountPostLikesAsync(seed.Post.Id));

        var authorCookie = await environment.LoginAsync(seed.Author.Handle);
        var authorForm = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{seed.Post.Id}", authorCookie);
        using (var selfLike = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{seed.Post.Id}/like",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = authorForm.Token,
                       ["liked"] = "true"
                   },
                   authorCookie,
                   authorForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, selfLike.StatusCode);
            Assert.Contains("error=self-like", selfLike.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        Assert.Equal(0, await CountPostLikesAsync(seed.Post.Id));
    }

    [DatabaseFact]
    public async Task Comment_like_and_comment_delete_follow_permissions_and_confirmation()
    {
        var seed = await SeedAsync();
        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var memberCookie = await environment.LoginAsync(seed.Member.Handle);
        var memberForm = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{seed.Post.Id}", memberCookie);

        var authorCookie = await environment.LoginAsync(seed.Author.Handle);
        var authorForm = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{seed.Post.Id}", authorCookie);
        using (var liked = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/comments/{seed.Comment.Id}/like",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = authorForm.Token,
                       ["postId"] = seed.Post.Id.ToString(),
                       ["liked"] = "true"
                   },
                   authorCookie,
                   authorForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, liked.StatusCode);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            Assert.True(await db.CommentLikes.AnyAsync(like =>
                like.CommentId == seed.Comment.Id && like.UserId == seed.Author.Id));
            Assert.Equal(1, await db.Comments
                .Where(comment => comment.Id == seed.Comment.Id)
                .Select(comment => comment.LikeCount)
                .SingleAsync());
        }

        using (var missingConfirm = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/comments/{seed.Comment.Id}/delete",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = memberForm.Token,
                       ["postId"] = seed.Post.Id.ToString(),
                       ["version"] = "1"
                   },
                   memberCookie,
                   memberForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, missingConfirm.StatusCode);
            Assert.Contains("error=delete-confirm", missingConfirm.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var comment = await db.Comments.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.Comment.Id);
            Assert.Null(comment.DeletedAt);
        }

        using (var authorAttempt = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/comments/{seed.Comment.Id}/delete",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = authorForm.Token,
                       ["postId"] = seed.Post.Id.ToString(),
                       ["version"] = "1",
                       ["confirm"] = "yes"
                   },
                   authorCookie,
                   authorForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, authorAttempt.StatusCode);
            Assert.Contains("error=forbidden", authorAttempt.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var comment = await db.Comments.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.Comment.Id);
            Assert.Null(comment.DeletedAt);
        }

        using (var deleted = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/comments/{seed.Comment.Id}/delete",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = memberForm.Token,
                       ["postId"] = seed.Post.Id.ToString(),
                       ["version"] = "1",
                       ["confirm"] = "yes"
                   },
                   memberCookie,
                   memberForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
            Assert.Contains("message=comment-deleted", deleted.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var comment = await db.Comments.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.Comment.Id);
            Assert.NotNull(comment.DeletedAt);
            Assert.Equal(ModerationStatuses.Deleted, comment.ModerationStatus);
        }
    }

    [DatabaseFact]
    public async Task Acceptance_is_limited_to_post_author_and_clears_solved_state()
    {
        var seed = await SeedAsync();
        var client = WebFormTestSupport.CreateClient(environment.WebFactory);

        var memberCookie = await environment.LoginAsync(seed.Member.Handle);
        var memberForm = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{seed.Post.Id}", memberCookie);
        using (var forbidden = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/comments/{seed.Comment.Id}/accept",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = memberForm.Token,
                       ["postId"] = seed.Post.Id.ToString()
                   },
                   memberCookie,
                   memberForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, forbidden.StatusCode);
            Assert.Contains("error=accept-forbidden", forbidden.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        var adminCookie = await environment.LoginAsync(seed.Admin.Handle);
        var adminForm = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{seed.Post.Id}", adminCookie);
        using (var adminAttempt = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/comments/{seed.Comment.Id}/accept",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = adminForm.Token,
                       ["postId"] = seed.Post.Id.ToString()
                   },
                   adminCookie,
                   adminForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, adminAttempt.StatusCode);
            Assert.Contains("error=accept-forbidden", adminAttempt.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        var authorCookie = await environment.LoginAsync(seed.Author.Handle);
        var authorForm = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{seed.Post.Id}", authorCookie);
        using (var accepted = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/comments/{seed.Comment.Id}/accept",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = authorForm.Token,
                       ["postId"] = seed.Post.Id.ToString()
                   },
                   authorCookie,
                   authorForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
            Assert.Contains("message=accepted", accepted.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var post = await db.Posts.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.Post.Id);
            var comment = await db.Comments.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.Comment.Id);
            Assert.True(post.IsSolved);
            Assert.Equal(seed.Comment.Id, post.AcceptedCommentId);
            Assert.True(comment.IsAccepted);
        }

        using (var unaccepted = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/comments/{seed.Comment.Id}/unaccept",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = authorForm.Token,
                       ["postId"] = seed.Post.Id.ToString()
                   },
                   authorCookie,
                   authorForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, unaccepted.StatusCode);
            Assert.Contains("message=unaccepted", unaccepted.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var post = await db.Posts.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.Post.Id);
            var comment = await db.Comments.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.Comment.Id);
            Assert.False(post.IsSolved);
            Assert.Null(post.AcceptedCommentId);
            Assert.False(comment.IsAccepted);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var ownComment = ServiceTestSupport.NewComment(seed.Post.Id, seed.Author.Id, body: "作者自己的评论");
            db.Comments.Add(ownComment);
            await db.SaveChangesAsync();
            seed = seed with { OwnCommentId = ownComment.Id };
        }

        using (var selfAccept = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/comments/{seed.OwnCommentId}/accept",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = authorForm.Token,
                       ["postId"] = seed.Post.Id.ToString()
                   },
                   authorCookie,
                   authorForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, selfAccept.StatusCode);
            Assert.Contains("error=self-accept", selfAccept.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }
    }

    [DatabaseFact]
    public async Task Reports_cover_post_comment_and_user_targets_and_validate_reason()
    {
        var seed = await SeedAsync();
        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var memberCookie = await environment.LoginAsync(seed.Member.Handle);
        var formContext = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{seed.Post.Id}", memberCookie);

        var targets = new[]
        {
            (Type: ReportTargetTypes.Post, Id: seed.Post.Id),
            (Type: ReportTargetTypes.Comment, Id: seed.Comment.Id),
            (Type: ReportTargetTypes.User, Id: seed.Author.Id)
        };
        foreach (var target in targets)
        {
            using var response = await WebFormTestSupport.PostFormAsync(
                client,
                "/reports",
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = formContext.Token,
                    ["targetType"] = target.Type,
                    ["targetId"] = target.Id.ToString(),
                    ["reason"] = ReportReasons.Spam,
                    ["detail"] = "测试举报说明",
                    ["returnUrl"] = $"/posts/{seed.Post.Id}"
                },
                memberCookie,
                formContext.CsrfCookie);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("message=report-submitted", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var reports = await db.Reports
                .AsNoTracking()
                .Where(report => report.ReporterId == seed.Member.Id)
                .ToListAsync();
            Assert.Equal(3, reports.Count);
            Assert.All(reports, report => Assert.Equal(ReportStatuses.Pending, report.Status));
            Assert.Contains(reports, report => report.TargetType == ReportTargetTypes.Post && report.TargetId == seed.Post.Id);
            Assert.Contains(reports, report => report.TargetType == ReportTargetTypes.Comment && report.TargetId == seed.Comment.Id);
            Assert.Contains(reports, report => report.TargetType == ReportTargetTypes.User && report.TargetId == seed.Author.Id);
        }

        using (var invalid = await WebFormTestSupport.PostFormAsync(
                   client,
                   "/reports",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = formContext.Token,
                       ["targetType"] = ReportTargetTypes.Post,
                       ["targetId"] = seed.Post.Id.ToString(),
                       ["reason"] = "not-a-reason",
                       ["returnUrl"] = $"/posts/{seed.Post.Id}"
                   },
                   memberCookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, invalid.StatusCode);
            Assert.Contains("error=report-invalid", invalid.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            Assert.Equal(
                3,
                await db.Reports.AsNoTracking().CountAsync(report => report.ReporterId == seed.Member.Id));
        }

        using (var anonymous = await WebFormTestSupport.PostFormAsync(
                   client,
                   "/reports",
                   new Dictionary<string, string>
                   {
                       ["targetType"] = ReportTargetTypes.Post,
                       ["targetId"] = seed.Post.Id.ToString(),
                       ["reason"] = ReportReasons.Spam
                   },
                   string.Empty,
                   string.Empty))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Contains("/login", anonymous.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            Assert.Equal(
                3,
                await db.Reports.AsNoTracking().CountAsync(report => report.ReporterId == seed.Member.Id));
        }
    }

    [DatabaseFact]
    public async Task Edit_page_enforces_permissions_version_conflict_and_resubmission()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var author = ServiceTestSupport.NewUser("edit-author");
        author.Handle = $"edit-author-{suffix}";
        var outsider = ServiceTestSupport.NewUser("edit-outsider");
        outsider.Handle = $"edit-outsider-{suffix}";
        var admin = ServiceTestSupport.NewUser("edit-admin", UserRoles.Admin);
        admin.Handle = $"edit-admin-{suffix}";
        var category = ServiceTestSupport.NewCategory($"编辑板块{suffix}");
        var tag = ServiceTestSupport.NewTag();
        tag.Name = $"编辑标签{suffix}";
        var post = ServiceTestSupport.NewPost(author.Id, category.Id, title: $"编辑测试帖子 {suffix}");
        var artifact = new Artifact
        {
            PostId = post.Id,
            Kind = "shell",
            Name = "保留产物",
            Content = "echo keep",
            SortOrder = 0
        };
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.AddRange(author, outsider, admin);
            db.Categories.Add(category);
            db.Tags.Add(tag);
            db.Posts.Add(post);
            db.Artifacts.Add(artifact);
            await db.SaveChangesAsync();
        }

        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var authorCookie = await environment.LoginAsync(author.Handle);
        using (var editPage = await GetWithCookieAsync(client, $"/posts/{post.Id}/edit", authorCookie))
        {
            Assert.Equal(HttpStatusCode.OK, editPage.StatusCode);
            var html = WebUtility.HtmlDecode(await editPage.Content.ReadAsStringAsync());
            Assert.Contains(post.Title, html, StringComparison.Ordinal);
            Assert.Contains(post.BodyMd, html, StringComparison.Ordinal);
            Assert.Contains(tag.Slug, html, StringComparison.Ordinal);
        }

        var outsiderCookie = await environment.LoginAsync(outsider.Handle);
        using (var forbiddenPage = await GetWithCookieAsync(client, $"/posts/{post.Id}/edit", outsiderCookie))
        {
            Assert.Equal(HttpStatusCode.NotFound, forbiddenPage.StatusCode);
        }

        var formContext = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{post.Id}/edit", authorCookie);
        var baseForm = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = formContext.Token,
            ["categoryId"] = category.Id.ToString(),
            ["originalCategoryId"] = category.Id.ToString(),
            ["title"] = $"更新后的标题 {suffix}",
            ["bodyMd"] = "更新后的正文内容，长度足够并通过审核。",
            ["kernel"] = "6.8.0",
            ["platform"] = "linux-x64",
            ["model"] = "deepseek-flash",
            ["tags"] = tag.Slug
        };

        using (var noToken = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{post.Id}/edit/submit",
                   new Dictionary<string, string>(baseForm.Where(pair => pair.Key != "__RequestVerificationToken"))
                   {
                       ["version"] = "1"
                   },
                   authorCookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        }

        using (var conflict = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{post.Id}/edit/submit",
                   new Dictionary<string, string>(baseForm) { ["version"] = "999" },
                   authorCookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var stored = await db.Posts.AsNoTracking().SingleAsync(candidate => candidate.Id == post.Id);
            Assert.Equal(post.Title, stored.Title);
            Assert.Equal(ModerationStatuses.Approved, stored.ModerationStatus);
        }

        using (var updated = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{post.Id}/edit/submit",
                   new Dictionary<string, string>(baseForm) { ["version"] = "1" },
                   authorCookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
            Assert.Contains("message=post-updated", updated.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var stored = await db.Posts.AsNoTracking().SingleAsync(candidate => candidate.Id == post.Id);
            Assert.Equal($"更新后的标题 {suffix}", stored.Title);
            Assert.Equal(ModerationStatuses.Pending, stored.ModerationStatus);
            Assert.Equal(2, stored.Version);
            Assert.True(await db.Artifacts.AsNoTracking().AnyAsync(candidate => candidate.Id == artifact.Id));
            var tagIds = await db.PostTags
                .AsNoTracking()
                .Where(postTag => postTag.PostId == post.Id)
                .Select(postTag => postTag.TagId)
                .ToListAsync();
            Assert.Equal(new[] { tag.Id }, tagIds);
        }

        var adminPost = ServiceTestSupport.NewPost(author.Id, category.Id, title: $"管理员编辑目标 {suffix}");
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Posts.Add(adminPost);
            await db.SaveChangesAsync();
        }

        var adminCookie = await environment.LoginAsync(admin.Handle);
        var adminForm = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{adminPost.Id}/edit", adminCookie);
        var adminBaseForm = new Dictionary<string, string>(baseForm)
        {
            ["__RequestVerificationToken"] = adminForm.Token,
            ["version"] = adminPost.Version.ToString()
        };
        using (var missingReason = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{adminPost.Id}/edit/submit",
                   new Dictionary<string, string>(adminBaseForm)
                   {
                       ["title"] = $"管理员修改标题 {suffix}"
                   },
                   adminCookie,
                   adminForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, missingReason.StatusCode);
            Assert.Contains("error=validation", missingReason.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        using (var adminUpdated = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{adminPost.Id}/edit/submit",
                   new Dictionary<string, string>(adminBaseForm)
                   {
                       ["title"] = $"管理员修改标题 {suffix}",
                       ["reason"] = "管理员修正标题"
                   },
                   adminCookie,
                   adminForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, adminUpdated.StatusCode);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var stored = await db.Posts.AsNoTracking().SingleAsync(candidate => candidate.Id == adminPost.Id);
            Assert.Equal($"管理员修改标题 {suffix}", stored.Title);
            Assert.Equal(ModerationStatuses.Approved, stored.ModerationStatus);
        }
    }

    [DatabaseFact]
    public async Task Rejected_post_shows_reason_and_resubmits_as_pending()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var author = ServiceTestSupport.NewUser("reject-author");
        author.Handle = $"reject-author-{suffix}";
        var category = ServiceTestSupport.NewCategory($"驳回板块{suffix}");
        var post = ServiceTestSupport.NewPost(
            author.Id,
            category.Id,
            ModerationStatuses.Rejected,
            title: $"被驳回的帖子 {suffix}");
        var reason = $"内容缺少复现步骤 {suffix}";
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.Add(author);
            db.Categories.Add(category);
            db.Posts.Add(post);
            db.ModerationActions.Add(new ModerationAction
            {
                TargetType = "post",
                TargetId = post.Id,
                ActorType = ModerationActorTypes.User,
                Action = "reject",
                FromStatus = ModerationStatuses.Pending,
                ToStatus = ModerationStatuses.Rejected,
                Reason = reason,
                ContentVersion = post.Version,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1)
            });
            await db.SaveChangesAsync();
        }

        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var cookie = await environment.LoginAsync(author.Handle);
        using (var editPage = await GetWithCookieAsync(client, $"/posts/{post.Id}/edit", cookie))
        {
            Assert.Equal(HttpStatusCode.OK, editPage.StatusCode);
            var html = WebUtility.HtmlDecode(await editPage.Content.ReadAsStringAsync());
            Assert.Contains(reason, html, StringComparison.Ordinal);
            Assert.Contains("驳回", html, StringComparison.Ordinal);
        }

        var formContext = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{post.Id}/edit", cookie);
        using (var updated = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{post.Id}/edit/submit",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = formContext.Token,
                       ["version"] = post.Version.ToString(),
                       ["categoryId"] = category.Id.ToString(),
                       ["originalCategoryId"] = category.Id.ToString(),
                       ["title"] = $"修改后的帖子 {suffix}",
                       ["bodyMd"] = "补充了完整的复现步骤与观察结果。",
                       ["kernel"] = "",
                       ["platform"] = "",
                       ["model"] = ""
                   },
                   cookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var stored = await db.Posts.AsNoTracking().SingleAsync(candidate => candidate.Id == post.Id);
            Assert.Equal(ModerationStatuses.Pending, stored.ModerationStatus);
            Assert.Equal($"修改后的帖子 {suffix}", stored.Title);
        }
    }

    [DatabaseFact]
    public async Task Like_is_blocked_for_muted_and_unactivated_accounts()
    {
        var seed = await SeedAsync();
        var muted = ServiceTestSupport.NewUser("interaction-muted");
        var pending = ServiceTestSupport.NewUser("interaction-pending", activated: false);
        var banned = ServiceTestSupport.NewUser("interaction-banned");
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.AddRange(muted, pending, banned);
            await db.SaveChangesAsync();
        }

        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var mutedCookie = await environment.LoginAsync(muted.Handle);
        var mutedForm = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{seed.Post.Id}", mutedCookie);
        await using (var db = ServiceTestSupport.CreateContext())
        {
            var stored = await db.Users.SingleAsync(candidate => candidate.Id == muted.Id);
            stored.Status = UserStatuses.Muted;
            stored.StatusExpiresAt = DateTimeOffset.UtcNow.AddDays(1);
            stored.StatusReason = "测试禁言";
            await db.SaveChangesAsync();
        }

        using (var mutedLike = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{seed.Post.Id}/like",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = mutedForm.Token,
                       ["liked"] = "true"
                   },
                   mutedCookie,
                   mutedForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, mutedLike.StatusCode);
        }

        var pendingCookie = await environment.LoginAsync(pending.Handle);
        var pendingForm = await WebFormTestSupport.LoadFormAsync(client, "/activate", pendingCookie);
        using (var pendingLike = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{seed.Post.Id}/like",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = pendingForm.Token,
                       ["liked"] = "true"
                   },
                   pendingCookie,
                   pendingForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, pendingLike.StatusCode);
        }

        var bannedCookie = await environment.LoginAsync(banned.Handle);
        await using (var db = ServiceTestSupport.CreateContext())
        {
            var stored = await db.Users.SingleAsync(candidate => candidate.Id == banned.Id);
            stored.Status = UserStatuses.Banned;
            stored.StatusReason = "测试封禁";
            await db.SaveChangesAsync();
        }

        using (var bannedLike = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{seed.Post.Id}/like",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = pendingForm.Token,
                       ["liked"] = "true"
                   },
                   bannedCookie,
                   pendingForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, bannedLike.StatusCode);
            Assert.Equal("/activate", bannedLike.Headers.Location?.OriginalString);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            Assert.False(await db.PostLikes.AsNoTracking().AnyAsync(like =>
                like.PostId == seed.Post.Id
                && (like.UserId == muted.Id || like.UserId == pending.Id || like.UserId == banned.Id)));
        }
    }

    [DatabaseFact]
    public async Task Muted_member_can_still_report_content()
    {
        var seed = await SeedAsync();
        var muted = ServiceTestSupport.NewUser(
            "interaction-muted-reporter",
            status: UserStatuses.Muted,
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(1),
            statusReason: "测试禁言");
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.Add(muted);
            await db.SaveChangesAsync();
        }

        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var cookie = await environment.LoginAsync(muted.Handle);
        var formContext = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{seed.Post.Id}", cookie);
        using (var reported = await WebFormTestSupport.PostFormAsync(
                   client,
                   "/reports",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = formContext.Token,
                       ["targetType"] = ReportTargetTypes.Post,
                       ["targetId"] = seed.Post.Id.ToString(),
                       ["reason"] = ReportReasons.Spam,
                       ["detail"] = "禁言用户举报测试",
                       ["returnUrl"] = $"/posts/{seed.Post.Id}"
                   },
                   cookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, reported.StatusCode);
            Assert.Contains("message=report-submitted", reported.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            Assert.True(await db.Reports.AsNoTracking().AnyAsync(report =>
                report.ReporterId == muted.Id && report.TargetId == seed.Post.Id));
        }
    }

    [DatabaseFact]
    public async Task Admin_comment_delete_requires_reason_and_accepted_comment_clears_solved()
    {
        var seed = await SeedAsync();
        await using (var db = ServiceTestSupport.CreateContext())
        {
            var post = await db.Posts.SingleAsync(candidate => candidate.Id == seed.Post.Id);
            var comment = await db.Comments.SingleAsync(candidate => candidate.Id == seed.Comment.Id);
            post.IsSolved = true;
            post.AcceptedCommentId = comment.Id;
            post.Version++;
            comment.IsAccepted = true;
            comment.Version++;
            await db.SaveChangesAsync();
        }

        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var adminCookie = await environment.LoginAsync(seed.Admin.Handle);
        var adminForm = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{seed.Post.Id}", adminCookie);

        using (var missingReason = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/comments/{seed.Comment.Id}/delete",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = adminForm.Token,
                       ["postId"] = seed.Post.Id.ToString(),
                       ["version"] = "2",
                       ["confirm"] = "yes"
                   },
                   adminCookie,
                   adminForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, missingReason.StatusCode);
            Assert.Contains("error=delete-invalid", missingReason.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var comment = await db.Comments.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.Comment.Id);
            Assert.Null(comment.DeletedAt);
            var post = await db.Posts.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.Post.Id);
            Assert.True(post.IsSolved);
        }

        using (var deleted = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/comments/{seed.Comment.Id}/delete",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = adminForm.Token,
                       ["postId"] = seed.Post.Id.ToString(),
                       ["version"] = "2",
                       ["confirm"] = "yes",
                       ["reason"] = "管理员删除违规评论"
                   },
                   adminCookie,
                   adminForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
            Assert.Contains("message=comment-deleted-solved", deleted.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var comment = await db.Comments.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.Comment.Id);
            var post = await db.Posts.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.Post.Id);
            Assert.NotNull(comment.DeletedAt);
            Assert.False(post.IsSolved);
            Assert.Null(post.AcceptedCommentId);
        }
    }

    private async Task<SeedData> SeedAsync()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var author = ServiceTestSupport.NewUser("interaction-author");
        author.Handle = $"interaction-author-{suffix}";
        var member = ServiceTestSupport.NewUser("interaction-member");
        member.Handle = $"interaction-member-{suffix}";
        var admin = ServiceTestSupport.NewUser("interaction-admin", UserRoles.Admin);
        admin.Handle = $"interaction-admin-{suffix}";
        var category = ServiceTestSupport.NewCategory($"互动板块{suffix}");
        var post = ServiceTestSupport.NewPost(author.Id, category.Id, title: $"互动测试帖子 {suffix}");
        var comment = ServiceTestSupport.NewComment(post.Id, member.Id, body: $"互动测试评论 {suffix}");

        await using var db = ServiceTestSupport.CreateContext();
        db.Users.AddRange(author, member, admin);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.Add(comment);
        await db.SaveChangesAsync();
        return new SeedData(author, member, admin, category, post, comment, Guid.Empty);
    }

    private static async Task<int> CountPostLikesAsync(Guid postId)
    {
        await using var db = ServiceTestSupport.CreateContext();
        return await db.PostLikes.AsNoTracking().CountAsync(like => like.PostId == postId);
    }

    private static async Task<HttpResponseMessage> GetWithCookieAsync(
        HttpClient client,
        string path,
        string sessionCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", sessionCookie);
        return await client.SendAsync(request);
    }

    private sealed record SeedData(
        User Author,
        User Member,
        User Admin,
        Category Category,
        Post Post,
        Comment Comment,
        Guid OwnCommentId);
}