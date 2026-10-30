using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class AgentHttpTests(ApiHttpEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ModelName = "deepseek-flash";
    private const string ModelVersion = "v1";
    private const string ModelQuantization = "q4";

    [DatabaseFact]
    public async Task Agent_auth_scope_ip_and_ai_gate_are_enforced()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: false);
        try
        {
            using var client = CreateClient();
            using (var disabled = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/policy",
                       setup.Token,
                       null,
                       null))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
                Assert.Equal(ApiErrorCodes.AgentDisabled, await ReadCodeAsync(disabled));
            }

            await EnableAiAsync(db, setup.Admin);
            using (var policy = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/policy",
                       setup.Token,
                       null,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
                using var document = JsonDocument.Parse(await policy.Content.ReadAsStringAsync());
                Assert.True(document.RootElement.GetProperty("data").GetProperty("enabled").GetBoolean());
            }

            using (var invalid = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/policy",
                       "wp_invalid_token",
                       null,
                       null))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
                Assert.Equal(ApiErrorCodes.ServiceTokenInvalid, await ReadCodeAsync(invalid));
            }

            var memberSession = await environment.LoginAsync(setup.Author.Handle);
            using (var cookie = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/policy",
                       null,
                       null,
                       null,
                       memberSession))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, cookie.StatusCode);
                Assert.Equal(ApiErrorCodes.ServiceTokenInvalid, await ReadCodeAsync(cookie));
            }

            var readOnlyToken = (await CreateTokenAsync(
                db,
                setup.Admin,
                "agent-read-only",
                ["moderation:read"])).Token;
            using (var scopeDenied = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{Guid.NewGuid()}/claim",
                       readOnlyToken,
                       Guid.NewGuid().ToString(),
                       null))
            {
                Assert.Equal(HttpStatusCode.Forbidden, scopeDenied.StatusCode);
                Assert.Equal(ApiErrorCodes.ScopeDenied, await ReadCodeAsync(scopeDenied));
            }

            var ipDeniedToken = (await CreateTokenAsync(
                db,
                setup.Admin,
                "agent-ip-denied",
                ["moderation:read", "moderation:suggest", "agent:heartbeat"],
                ["10.0.0.1"])).Token;
            using (var ipDenied = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/policy",
                       ipDeniedToken,
                       null,
                       null))
            {
                Assert.Equal(HttpStatusCode.Forbidden, ipDenied.StatusCode);
                Assert.Equal(ApiErrorCodes.Forbidden, await ReadCodeAsync(ipDenied));
            }

            var expiring = (await CreateTokenAsync(
                db,
                setup.Admin,
                "agent-expiry",
                ["moderation:read"])).Token;
            var expiringPrefix = expiring[..12];
            var expiringEntity = await db.ServiceTokens
                .SingleAsync(token => token.Prefix == expiringPrefix);
            expiringEntity.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
            expiringEntity.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
            using (var expired = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/policy",
                       expiring,
                       null,
                       null))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
                Assert.Equal(ApiErrorCodes.ServiceTokenExpired, await ReadCodeAsync(expired));
            }
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    [DatabaseFact]
    public async Task Service_account_token_budget_override_blocks_after_usage()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: true);
        try
        {
            using var client = CreateClient();
            var instanceId = await HeartbeatAsync(client, setup.Token);
            var suffix = ServiceTestSupport.NewSuffix();
            var secondPost = ServiceTestSupport.NewPost(
                setup.Author.Id,
                setup.Post.CategoryId,
                ModerationStatuses.Pending,
                title: $"服务账号预算测试标题 {suffix}",
                body: "这是服务账号预算覆盖 HTTP 测试正文，长度足够。");
            db.Posts.Add(secondPost);
            await db.SaveChangesAsync();
            var firstTask = new ModerationTask
            {
                TargetType = ModerationTargetTypes.Post,
                TargetId = setup.Post.Id,
                ContentVersion = setup.Post.Version,
                ContentHash = AgentContentHash.ForPost(setup.Post),
                Status = ModerationTaskStatuses.Queued,
                Priority = 20
            };
            var secondTask = new ModerationTask
            {
                TargetType = ModerationTargetTypes.Post,
                TargetId = secondPost.Id,
                ContentVersion = secondPost.Version,
                ContentHash = AgentContentHash.ForPost(secondPost),
                Status = ModerationTaskStatuses.Queued,
                Priority = 10
            };
            db.ModerationTasks.AddRange(firstTask, secondTask);
            var account = await db.ServiceAccounts.SingleAsync(item => item.Id == setup.AccountId);
            account.DailyTokenBudget = 1;
            await db.SaveChangesAsync();

            using (var claim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{firstTask.Id}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            }

            using (var suggestion = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{firstTask.Id}/suggestion",
                       setup.Token,
                       instanceId,
                       NewSuggestionRequest(firstTask.Id, 1, $"account-budget-{suffix}")))
            {
                Assert.Equal(HttpStatusCode.Created, suggestion.StatusCode);
            }

            using (var blocked = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{secondTask.Id}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
                var body = await blocked.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(body);
                Assert.Equal(
                    ApiErrorCodes.AgentRateLimited,
                    document.RootElement.GetProperty("code").GetString());
                Assert.Contains(
                    "service_account_token_budget_exceeded",
                    body,
                    StringComparison.Ordinal);
            }

            db.ChangeTracker.Clear();
            Assert.Equal(
                0,
                await db.ModerationSuggestions.CountAsync(item => item.TaskId == secondTask.Id));
            Assert.True(await db.AuditLogs.AnyAsync(log =>
                log.Action == AiBudgetService.AccountTokenBudgetExceededAction
                && log.ActorId == setup.AccountId));
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    [DatabaseFact]
    public async Task Invalid_suggestion_records_redacted_failure_audit()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: true);
        try
        {
            using var client = CreateClient();
            var instanceId = await HeartbeatAsync(client, setup.Token);
            var suffix = ServiceTestSupport.NewSuffix();
            var task = new ModerationTask
            {
                TargetType = ModerationTargetTypes.Post,
                TargetId = setup.Post.Id,
                ContentVersion = setup.Post.Version,
                ContentHash = AgentContentHash.ForPost(setup.Post),
                Status = ModerationTaskStatuses.Queued,
                Priority = 10
            };
            db.ModerationTasks.Add(task);
            await db.SaveChangesAsync();

            using (var invalid = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{task.Id}/suggestion",
                       setup.Token,
                       instanceId,
                       new
                       {
                           suggestion = "maybe",
                           policyCode = "manual_review",
                           reason = "非法建议测试",
                           confidence = 0.4,
                           contentVersion = 1,
                           policyVersion = AgentPolicyDefaults.Version,
                           model = new
                           {
                               name = ModelName,
                               version = ModelVersion,
                               quantization = ModelQuantization,
                               runtime = "local"
                           },
                           modelRuntime = "local",
                           runtimeLocation = "local",
                           promptVersion = "prompt-v1",
                           idempotencyKey = $"invalid-{suffix}",
                           latencyMs = 100,
                           tokenUsage = new { total = 1 }
                       }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Equal(ApiErrorCodes.SuggestionInvalid, await ReadCodeAsync(invalid));
            }

            db.ChangeTracker.Clear();
            var audit = await db.AuditLogs.SingleAsync(log =>
                log.Action == AiBudgetService.SuggestionFailedAction
                && log.ActorId == setup.AccountId
                && log.TargetId == task.Id);
            Assert.Equal("SUGGESTION_INVALID", audit.MetadataJson.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain(setup.Post.Title, audit.MetadataJson.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(setup.Post.BodyMd, audit.MetadataJson.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    [DatabaseFact]
    public async Task Daily_token_budget_blocks_new_claim_and_suggestion_until_budget_is_raised()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: true);
        try
        {
            using var client = CreateClient();
            var instanceId = await HeartbeatAsync(client, setup.Token);
            var suffix = ServiceTestSupport.NewSuffix();
            var secondPost = ServiceTestSupport.NewPost(
                setup.Author.Id,
                setup.Post.CategoryId,
                ModerationStatuses.Pending,
                title: $"Agent 预算测试标题 {suffix}",
                body: "这是用于预算门禁测试的正文，长度足够且需要人工裁决。");
            db.Posts.Add(secondPost);
            await db.SaveChangesAsync();

            var firstTask = new ModerationTask
            {
                TargetType = ModerationTargetTypes.Post,
                TargetId = setup.Post.Id,
                ContentVersion = setup.Post.Version,
                ContentHash = AgentContentHash.ForPost(setup.Post),
                Status = ModerationTaskStatuses.Queued,
                Priority = 20
            };
            var secondTask = new ModerationTask
            {
                TargetType = ModerationTargetTypes.Post,
                TargetId = secondPost.Id,
                ContentVersion = secondPost.Version,
                ContentHash = AgentContentHash.ForPost(secondPost),
                Status = ModerationTaskStatuses.Queued,
                Priority = 10
            };
            db.ModerationTasks.AddRange(firstTask, secondTask);
            await db.SaveChangesAsync();

            var settings = new AiSettingsService(
                db,
                new AdminAccessService(db),
                TimeProvider.System);
            var budgetService = new AiBudgetService(
                db,
                settings,
                TimeProvider.System,
                NullLogger<AiBudgetService>.Instance);
            var model = new ApprovedModel(ModelName, ModelVersion, ModelQuantization);
            var start = await budgetService.GetAsync();
            var initialBudget = (int)Math.Min(1_000_000_000, start.UsedTokensToday + 200);
            var opened = await settings.UpdateAsync(
                true,
                false,
                [model],
                initialBudget,
                false,
                ServiceTestSupport.NewAudit(setup.Admin.Id));
            Assert.True(opened.Outcome is AiSettingsOutcome.Success or AiSettingsOutcome.Unchanged);

            using (var claim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{firstTask.Id}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            }

            using (var suggestion = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{firstTask.Id}/suggestion",
                       setup.Token,
                       instanceId,
                       NewSuggestionRequest(firstTask.Id, 1, $"budget-ok-{suffix}")))
            {
                Assert.Equal(HttpStatusCode.Created, suggestion.StatusCode);
            }

            db.ChangeTracker.Clear();
            var usedAfterSuggestion = (await budgetService.GetAsync()).UsedTokensToday;
            Assert.True(usedAfterSuggestion >= 120);
            await settings.UpdateAsync(
                true,
                false,
                [model],
                (int)usedAfterSuggestion,
                false,
                ServiceTestSupport.NewAudit(setup.Admin.Id));

            using (var blockedClaim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{secondTask.Id}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, blockedClaim.StatusCode);
                Assert.Equal(ApiErrorCodes.AgentRateLimited, await ReadCodeAsync(blockedClaim));
                Assert.NotNull(blockedClaim.Headers.RetryAfter);
                Assert.True(blockedClaim.Headers.RetryAfter!.Delta > TimeSpan.Zero);
            }

            using (var blockedSuggestion = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{secondTask.Id}/suggestion",
                       setup.Token,
                       instanceId,
                       NewSuggestionRequest(secondTask.Id, 1, $"budget-blocked-{suffix}")))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, blockedSuggestion.StatusCode);
                Assert.Equal(ApiErrorCodes.AgentRateLimited, await ReadCodeAsync(blockedSuggestion));
            }

            db.ChangeTracker.Clear();
            Assert.Equal(
                ModerationTaskStatuses.Queued,
                (await db.ModerationTasks.AsNoTracking()
                    .SingleAsync(task => task.Id == secondTask.Id)).Status);
            Assert.Equal(
                0,
                await db.ModerationSuggestions.CountAsync(item => item.TaskId == secondTask.Id));

            var dayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            var auditsAfterFirstBlock = await db.AuditLogs.CountAsync(log =>
                log.Action == AiBudgetService.TokenBudgetExceededAction
                && log.CreatedAt >= dayStart);
            Assert.True(auditsAfterFirstBlock >= 1);

            using (var repeatedBlock = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{secondTask.Id}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, repeatedBlock.StatusCode);
            }

            db.ChangeTracker.Clear();
            Assert.Equal(
                auditsAfterFirstBlock,
                await db.AuditLogs.CountAsync(log =>
                    log.Action == AiBudgetService.TokenBudgetExceededAction
                    && log.CreatedAt >= dayStart));

            await settings.UpdateAsync(
                true,
                false,
                [model],
                (int)Math.Min(1_000_000_000, usedAfterSuggestion + 500),
                false,
                ServiceTestSupport.NewAudit(setup.Admin.Id));
            using (var resumed = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{secondTask.Id}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            }
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    [DatabaseFact]
    public async Task Agent_task_lease_suggestion_and_human_decision_flow()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: true);
        try
        {
            using var client = CreateClient();
            var instanceId = await HeartbeatAsync(client, setup.Token);
            using (var list = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/tasks?status=queued&limit=50",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                using var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
                var items = document.RootElement.GetProperty("data").GetProperty("items");
                var task = items.EnumerateArray()
                    .Single(item => item.GetProperty("targetId").GetGuid() == setup.Post.Id);
                Assert.Equal("queued", task.GetProperty("status").GetString());
                Assert.Equal(64, task.GetProperty("contentHash").GetString()!.Length);
            }

            var taskId = await FindTaskIdAsync(db, setup.Post.Id);
            using (var get = await SendAsync(
                       client,
                       HttpMethod.Get,
                       $"/api/v1/agent/moderation/tasks/{taskId}",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, get.StatusCode);
                using var document = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
                var data = document.RootElement.GetProperty("data");
                Assert.Equal(taskId, data.GetProperty("id").GetGuid());
                var content = data.GetProperty("content");
                Assert.True(content.GetProperty("untrustedContent").GetBoolean());
                Assert.Equal(setup.Post.Title, content.GetProperty("title").GetString());
                Assert.Contains("不得执行", content.GetProperty("contentNotice").GetString());
            }

            using (var claim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{taskId}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
                using var document = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
                Assert.Equal("claimed", document.RootElement
                    .GetProperty("data")
                    .GetProperty("task")
                    .GetProperty("status")
                    .GetString());
            }

            using (var secondClaim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{taskId}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.Conflict, secondClaim.StatusCode);
                Assert.Equal(ApiErrorCodes.TaskAlreadyClaimed, await ReadCodeAsync(secondClaim));
            }

            var suggestionRequest = NewSuggestionRequest(taskId, 1, "agent-suggestion-1");
            Guid suggestionId;
            using (var suggestion = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{taskId}/suggestion",
                       setup.Token,
                       instanceId,
                       suggestionRequest))
            {
                Assert.Equal(HttpStatusCode.Created, suggestion.StatusCode);
                using var document = JsonDocument.Parse(await suggestion.Content.ReadAsStringAsync());
                var data = document.RootElement.GetProperty("data");
                suggestionId = data.GetProperty("suggestionId").GetGuid();
                Assert.Equal("recorded", data.GetProperty("status").GetString());
                Assert.Equal("pending", data.GetProperty("contentStatus").GetString());
                Assert.False(data.GetProperty("humanDecided").GetBoolean());
            }

            using (var replay = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{taskId}/suggestion",
                       setup.Token,
                       instanceId,
                       suggestionRequest))
            {
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                using var document = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
                Assert.Equal(
                    suggestionId,
                    document.RootElement.GetProperty("data").GetProperty("suggestionId").GetGuid());
            }

            db.ChangeTracker.Clear();
            Assert.Equal(
                ModerationStatuses.Pending,
                (await db.Posts.AsNoTracking().SingleAsync(post => post.Id == setup.Post.Id)).ModerationStatus);
            Assert.Equal(
                ModerationTaskStatuses.Suggested,
                (await db.ModerationTasks.AsNoTracking().SingleAsync(task => task.Id == taskId)).Status);

            var services = ServiceTestSupport.CreateServices(db);
            var queue = await services.Moderation.GetQueueAsync(setup.Admin.Id, "post", 50, null);
            var queueItem = queue.Items.Single(item => item.TargetId == setup.Post.Id);
            Assert.NotNull(queueItem.AiSuggestion);
            Assert.Equal(suggestionId, queueItem.AiSuggestion!.Id);
            await services.Moderation.ExecuteAsync(
                setup.Admin.Id,
                "post",
                setup.Post.Id,
                new ModerationActionCommand(
                    ModerationActions.Approve,
                    "人工确认通过",
                    queueItem.ContentVersion,
                    suggestionId,
                    null),
                ServiceTestSupport.NewAudit(setup.Admin.Id));

            db.ChangeTracker.Clear();
            var approved = await db.Posts.AsNoTracking().SingleAsync(post => post.Id == setup.Post.Id);
            Assert.Equal(ModerationStatuses.Approved, approved.ModerationStatus);
            var suggestionEntity = await db.ModerationSuggestions
                .AsNoTracking()
                .SingleAsync(item => item.Id == suggestionId);
            Assert.Equal(ModerationActions.Approve, suggestionEntity.HumanDecision);
            Assert.Equal(
                ModerationTaskStatuses.HumanDecided,
                (await db.ModerationTasks.AsNoTracking().SingleAsync(task => task.Id == taskId)).Status);
            var quality = await new AiQualityService(db).GetAsync();
            Assert.True(quality.Total >= 1);
            Assert.True(quality.Adopted >= 1);
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    [DatabaseFact]
    public async Task Agent_redacts_pii_secrets_reclaims_expired_lease_and_rejects_stale_version()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(
            db,
            enableAi: true,
            body: "联系 user@example.com，来源 IP 192.168.1.10，令牌 sk-abcdefghijklmnop1234567890。");
        try
        {
            using var client = CreateClient();
            var instanceId = await HeartbeatAsync(client, setup.Token);
            using (var list = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/tasks?status=queued&limit=50",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            }

            var taskId = await FindTaskIdAsync(db, setup.Post.Id);
            using (var get = await SendAsync(
                       client,
                       HttpMethod.Get,
                       $"/api/v1/agent/moderation/tasks/{taskId}",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, get.StatusCode);
                var body = await get.Content.ReadAsStringAsync();
                Assert.Contains("[REDACTED:email]", body);
                Assert.Contains("[REDACTED:ip]", body);
                Assert.Contains("[REDACTED:secret]", body);
                Assert.DoesNotContain("user@example.com", body);
                Assert.DoesNotContain("192.168.1.10", body);
                Assert.DoesNotContain("sk-abcdefghijklmnop", body);
            }

            using (var claim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{taskId}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            }

            var taskEntity = await db.ModerationTasks.SingleAsync(task => task.Id == taskId);
            taskEntity.ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            taskEntity.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            using (var reclaim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{taskId}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, reclaim.StatusCode);
            }

            var postEntity = await db.Posts.SingleAsync(post => post.Id == setup.Post.Id);
            postEntity.Version++;
            postEntity.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            using (var stale = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{taskId}/suggestion",
                       setup.Token,
                       instanceId,
                       NewSuggestionRequest(taskId, 1, "agent-stale-1")))
            {
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                Assert.Equal(ApiErrorCodes.ContentVersionStale, await ReadCodeAsync(stale));
            }
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    [DatabaseFact]
    public async Task Agent_pause_keeps_heartbeat_policy_and_reads_but_blocks_claim()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: true);
        try
        {
            using var client = CreateClient();
            var instanceId = await HeartbeatAsync(client, setup.Token);
            using (var listBeforePause = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/tasks?status=queued&limit=50",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, listBeforePause.StatusCode);
            }

            var taskId = await FindTaskIdAsync(db, setup.Post.Id);
            await SetPausedAsync(db, setup.Admin, paused: true);
            using (var heartbeat = await SendAsync(
                       client,
                       HttpMethod.Post,
                       "/api/v1/agent/heartbeat",
                       setup.Token,
                       null,
                       new
                       {
                           instanceName = $"agent-paused-{Guid.NewGuid():N}"[..20],
                           hostFingerprint = new string('b', 64),
                           agentVersion = "0.1.0",
                           mcpVersion = "0.1.0",
                           runtimeLocation = "local",
                           model = new
                           {
                               name = ModelName,
                               version = ModelVersion,
                               quantization = ModelQuantization,
                               runtime = "local"
                           },
                           queueDepth = 0
                       }))
            {
                Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
                using var document = JsonDocument.Parse(await heartbeat.Content.ReadAsStringAsync());
                Assert.True(document.RootElement
                    .GetProperty("data")
                    .GetProperty("suggestionsPaused")
                    .GetBoolean());
            }

            using (var policy = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/policy",
                       setup.Token,
                       null,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
                using var document = JsonDocument.Parse(await policy.Content.ReadAsStringAsync());
                Assert.True(document.RootElement
                    .GetProperty("data")
                    .GetProperty("suggestionsPaused")
                    .GetBoolean());
            }

            using (var list = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/tasks?status=queued",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            }

            using (var claim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{taskId}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, claim.StatusCode);
                Assert.Equal(ApiErrorCodes.AgentDisabled, await ReadCodeAsync(claim));
            }

            using (var unapprovedModel = await SendAsync(
                       client,
                       HttpMethod.Post,
                       "/api/v1/agent/heartbeat",
                       setup.Token,
                       null,
                       new
                       {
                           instanceName = $"agent-model-{Guid.NewGuid():N}"[..20],
                           hostFingerprint = new string('c', 64),
                           agentVersion = "0.1.0",
                           runtimeLocation = "local",
                           model = new
                           {
                               name = "remote-model",
                               version = "v1",
                               quantization = "q4",
                               runtime = "local"
                           },
                           queueDepth = 0
                       }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, unapprovedModel.StatusCode);
            }

            await SetPausedAsync(db, setup.Admin, paused: false);
            using (var resumedClaim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{taskId}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, resumedClaim.StatusCode);
            }
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    private async Task<Guid> FindTaskIdAsync(WhalePodDbContext db, Guid postId)
    {
        db.ChangeTracker.Clear();
        return await db.ModerationTasks
            .AsNoTracking()
            .Where(task => task.TargetType == ModerationTargetTypes.Post
                && task.TargetId == postId)
            .OrderByDescending(task => task.CreatedAt)
            .Select(task => task.Id)
            .FirstAsync();
    }

    [DatabaseFact]
    public async Task Concurrent_suggestions_respect_the_daily_task_budget()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: true);
        try
        {
            using var client = CreateClient();
            var instanceId = await HeartbeatAsync(client, setup.Token);
            var suffix = ServiceTestSupport.NewSuffix();
            var secondPost = ServiceTestSupport.NewPost(
                setup.Author.Id,
                setup.Post.CategoryId,
                ModerationStatuses.Pending,
                title: $"并发任务预算标题 {suffix}",
                body: "这是用于并发任务预算测试的正文，长度足够且需要人工裁决。");
            db.Posts.Add(secondPost);
            await db.SaveChangesAsync();
            var firstTask = NewTaskFor(setup.Post);
            var secondTask = NewTaskFor(secondPost);
            db.ModerationTasks.AddRange(firstTask, secondTask);
            await db.SaveChangesAsync();
            var dayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            var existingSuggestions = await db.ModerationSuggestions
                .CountAsync(suggestion => suggestion.CreatedAt >= dayStart);
            await SetAiBudgetsAsync(
                db,
                setup.Admin,
                dailyTokenBudget: await CurrentTokenBudgetAsync(db) + 10_000,
                dailyTaskBudget: existingSuggestions + 1,
                dailyContentBudget: 1_000);

            foreach (var task in new[] { firstTask, secondTask })
            {
                using var claim = await SendAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/v1/agent/moderation/tasks/{task.Id}/claim",
                    setup.Token,
                    instanceId,
                    null);
                Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            }

            var responses = await Task.WhenAll(
                SendAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/v1/agent/moderation/tasks/{firstTask.Id}/suggestion",
                    setup.Token,
                    instanceId,
                    NewSuggestionRequest(firstTask.Id, firstTask.ContentVersion, $"task-budget-a-{suffix}")),
                SendAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/v1/agent/moderation/tasks/{secondTask.Id}/suggestion",
                    setup.Token,
                    instanceId,
                    NewSuggestionRequest(secondTask.Id, secondTask.ContentVersion, $"task-budget-b-{suffix}")));
            try
            {
                var statuses = responses.Select(response => response.StatusCode).ToList();
                Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Created));
                Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.TooManyRequests));
                db.ChangeTracker.Clear();
                Assert.Equal(
                    1,
                    await db.ModerationSuggestions.CountAsync(suggestion =>
                        suggestion.TaskId == firstTask.Id || suggestion.TaskId == secondTask.Id));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    [DatabaseFact]
    public async Task Content_budget_truncates_generation_without_blocking_claimed_tasks()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: true);
        try
        {
            using var client = CreateClient();
            var instanceId = await HeartbeatAsync(client, setup.Token);
            var suffix = ServiceTestSupport.NewSuffix();
            var secondPost = ServiceTestSupport.NewPost(
                setup.Author.Id,
                setup.Post.CategoryId,
                ModerationStatuses.Pending,
                title: $"内容额度截断标题 {suffix}",
                body: "这是用于内容额度截断测试的正文，长度足够且需要人工裁决。");
            db.Posts.Add(secondPost);
            await db.SaveChangesAsync();
            var dayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            var existingTasks = await db.ModerationTasks
                .CountAsync(task => task.CreatedAt >= dayStart);
            await SetAiBudgetsAsync(
                db,
                setup.Admin,
                dailyTokenBudget: await CurrentTokenBudgetAsync(db) + 10_000,
                dailyTaskBudget: 1_000,
                dailyContentBudget: existingTasks + 1);

            using (var list = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/tasks?status=queued&limit=50",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            }

            db.ChangeTracker.Clear();
            var createdTasks = await db.ModerationTasks
                .AsNoTracking()
                .Where(task => task.CreatedAt >= dayStart)
                .ToListAsync();
            Assert.Equal(existingTasks + 1, createdTasks.Count);
            var task = createdTasks.OrderByDescending(candidate => candidate.CreatedAt)
                .First();

            using (var claim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{task.Id}/claim",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            }

            using (var suggestion = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{task.Id}/suggestion",
                       setup.Token,
                       instanceId,
                       NewSuggestionRequest(task.Id, task.ContentVersion, $"content-budget-{suffix}")))
            {
                Assert.Equal(HttpStatusCode.Created, suggestion.StatusCode);
            }
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    [DatabaseFact]
    public async Task Old_release_cannot_clear_a_new_lease_or_terminal_status()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: true);
        try
        {
            using var client = CreateClient();
            var firstInstance = await HeartbeatAsync(client, setup.Token);
            var secondInstance = await HeartbeatAsync(client, setup.Token);
            var task = NewTaskFor(setup.Post);
            db.ModerationTasks.Add(task);
            await db.SaveChangesAsync();

            using (var firstClaim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{task.Id}/claim",
                       setup.Token,
                       firstInstance,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, firstClaim.StatusCode);
            }

            db.ChangeTracker.Clear();
            var stored = await db.ModerationTasks.SingleAsync(candidate => candidate.Id == task.Id);
            stored.ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
            stored.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();

            using (var secondClaim = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{task.Id}/claim",
                       setup.Token,
                       secondInstance,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, secondClaim.StatusCode);
            }

            using (var staleRelease = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{task.Id}/release",
                       setup.Token,
                       firstInstance,
                       new { reason = "旧实例释放" }))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleRelease.StatusCode);
                Assert.Equal(ApiErrorCodes.TaskLeaseExpired, await ReadCodeAsync(staleRelease));
            }

            db.ChangeTracker.Clear();
            var afterRelease = await db.ModerationTasks.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == task.Id);
            Assert.Equal(ModerationTaskStatuses.Claimed, afterRelease.Status);
            Assert.Equal(Guid.Parse(secondInstance), afterRelease.ClaimedByAgentInstanceId);

            using (var suggestion = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{task.Id}/suggestion",
                       setup.Token,
                       secondInstance,
                       NewSuggestionRequest(task.Id, task.ContentVersion, $"lease-fence-{ServiceTestSupport.NewSuffix()}")))
            {
                Assert.Equal(HttpStatusCode.Created, suggestion.StatusCode);
            }

            using (var terminalRelease = await SendAsync(
                       client,
                       HttpMethod.Post,
                       $"/api/v1/agent/moderation/tasks/{task.Id}/release",
                       setup.Token,
                       secondInstance,
                       new { reason = "终态释放" }))
            {
                Assert.Equal(HttpStatusCode.Conflict, terminalRelease.StatusCode);
            }

            db.ChangeTracker.Clear();
            Assert.Equal(
                ModerationTaskStatuses.Suggested,
                (await db.ModerationTasks.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == task.Id)).Status);
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    [DatabaseFact]
    public async Task Task_cursor_preserves_sub_millisecond_created_at()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: true);
        try
        {
            using var client = CreateClient();
            var instanceId = await HeartbeatAsync(client, setup.Token);
            var suffix = ServiceTestSupport.NewSuffix();
            var secondPost = ServiceTestSupport.NewPost(
                setup.Author.Id,
                setup.Post.CategoryId,
                ModerationStatuses.Pending,
                title: $"游标精度标题 {suffix}",
                body: "这是用于游标精度测试的正文，长度足够且需要人工裁决。");
            db.Posts.Add(secondPost);
            await db.SaveChangesAsync();

            using (var list = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/agent/moderation/tasks?status=queued&limit=50",
                       setup.Token,
                       instanceId,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            }

            db.ChangeTracker.Clear();
            var tasks = await db.ModerationTasks
                .Where(task => task.TargetId == setup.Post.Id || task.TargetId == secondPost.Id)
                .OrderBy(task => task.Id)
                .ToListAsync();
            Assert.Equal(2, tasks.Count);
            var baseTime = new DateTimeOffset(DateTime.UtcNow.Date.AddHours(2), TimeSpan.Zero);
            tasks[0].CreatedAt = baseTime.AddTicks(10);
            tasks[1].CreatedAt = baseTime.AddTicks(20);
            tasks[0].Priority = 20;
            tasks[1].Priority = 20;
            await db.SaveChangesAsync();

            var seen = new List<Guid>();
            string? cursor = null;
            for (var page = 0; page < 2; page++)
            {
                var path = "/api/v1/agent/moderation/tasks?status=queued&limit=1"
                    + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
                using var response = await SendAsync(
                    client,
                    HttpMethod.Get,
                    path,
                    setup.Token,
                    instanceId,
                    null);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var data = document.RootElement.GetProperty("data");
                var item = Assert.Single(data.GetProperty("items").EnumerateArray());
                seen.Add(item.GetProperty("id").GetGuid());
                cursor = data.GetProperty("nextCursor").GetString();
                if (page == 1)
                {
                    Assert.Null(cursor);
                }
            }

            Assert.Equal(2, seen.Distinct().Count());
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    [DatabaseFact]
    public async Task Agent_task_detail_redacts_artifact_names()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: true);
        try
        {
            using var client = CreateClient();
            var instanceId = await HeartbeatAsync(client, setup.Token);
            db.Artifacts.Add(new Artifact
            {
                PostId = setup.Post.Id,
                Kind = ArtifactKinds.Markdown,
                Name = "联系 user@example.com",
                Note = "备用邮箱 user@example.com",
                Content = "运行说明，无敏感信息。"
            });
            var task = NewTaskFor(setup.Post);
            db.ModerationTasks.Add(task);
            await db.SaveChangesAsync();

            using var get = await SendAsync(
                client,
                HttpMethod.Get,
                $"/api/v1/agent/moderation/tasks/{task.Id}",
                setup.Token,
                instanceId,
                null);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var body = await get.Content.ReadAsStringAsync();
            Assert.Contains("[REDACTED:email]", body);
            Assert.DoesNotContain("user@example.com", body);
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    [DatabaseFact]
    public async Task Invalid_json_counts_toward_the_failure_circuit()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var setup = await PrepareAsync(db, enableAi: true);
        ModerationTask? task = null;
        try
        {
            using var client = CreateClient();
            var instanceId = await HeartbeatAsync(client, setup.Token);
            task = NewTaskFor(setup.Post);
            db.ModerationTasks.Add(task);
            await db.SaveChangesAsync();

            for (var index = 0; index < AiBudgetService.FailureThreshold; index++)
            {
                using var invalid = await SendRawAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/v1/agent/moderation/tasks/{task.Id}/suggestion",
                    setup.Token,
                    instanceId,
                    "{not-json");
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            }

            db.ChangeTracker.Clear();
            var failureMetadata = await db.AuditLogs
                .AsNoTracking()
                .Where(log => log.Action == AiBudgetService.SuggestionFailedAction
                    && log.TargetId == task.Id)
                .Select(log => log.MetadataJson)
                .ToListAsync();
            Assert.Equal(
                AiBudgetService.FailureThreshold,
                failureMetadata.Count(metadata =>
                    metadata.RootElement.GetProperty("code").GetString()
                        == ApiErrorCodes.ValidationFailed));

            using var blocked = await SendAsync(
                client,
                HttpMethod.Post,
                $"/api/v1/agent/moderation/tasks/{task.Id}/claim",
                setup.Token,
                instanceId,
                null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
            Assert.Equal(ApiErrorCodes.AgentDisabled, await ReadCodeAsync(blocked));
        }
        finally
        {
            await DisableAiAsync(db, setup.Admin);
        }
    }

    private static ModerationTask NewTaskFor(Post post)
    {
        return new ModerationTask
        {
            TargetType = ModerationTargetTypes.Post,
            TargetId = post.Id,
            ContentVersion = post.Version,
            ContentHash = AgentContentHash.ForPost(post),
            Status = ModerationTaskStatuses.Queued,
            Priority = 10
        };
    }

    private static async Task<int> CurrentTokenBudgetAsync(WhalePodDbContext db)
    {
        var settings = new AiSettingsService(db, new AdminAccessService(db), TimeProvider.System);
        var budget = new AiBudgetService(
            db,
            settings,
            TimeProvider.System,
            NullLogger<AiBudgetService>.Instance);
        var status = await budget.GetAsync();
        return (int)Math.Min(1_000_000_000, status.UsedTokensToday);
    }

    private static async Task SetAiBudgetsAsync(
        WhalePodDbContext db,
        User admin,
        int dailyTokenBudget,
        int dailyTaskBudget,
        int dailyContentBudget)
    {
        var settings = new AiSettingsService(db, new AdminAccessService(db), TimeProvider.System);
        var result = await settings.UpdateAsync(
            enabled: true,
            suggestionsPaused: false,
            approvedModels: [new ApprovedModel(ModelName, ModelVersion, ModelQuantization)],
            dailyTokenBudget: dailyTokenBudget,
            dailyTaskBudget: dailyTaskBudget,
            dailyContentBudget: dailyContentBudget,
            confirmEnable: true,
            audit: ServiceTestSupport.NewAudit(admin.Id));
        Assert.True(result.Outcome is AiSettingsOutcome.Success or AiSettingsOutcome.Unchanged);
    }

    private static async Task<HttpResponseMessage> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? token,
        string? instanceId,
        string body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            request.Headers.Add("X-Agent-Instance-Id", instanceId);
        }

        return await client.SendAsync(request);
    }

    private static object NewSuggestionRequest(Guid taskId, int contentVersion, string idempotencyKey)
    {
        _ = taskId;
        return new
        {
            suggestion = SuggestionTypes.Approve,
            policyCode = "manual_review",
            reason = "需要人工优先复核。",
            confidence = 0.74,
            contentVersion,
            policyVersion = AgentPolicyDefaults.Version,
            model = new
            {
                name = ModelName,
                version = ModelVersion,
                quantization = ModelQuantization,
                runtime = "local"
            },
            modelRuntime = "local",
            runtimeLocation = "local",
            promptVersion = "moderation-prompt-v1",
            idempotencyKey,
            latencyMs = 1200,
            tokenUsage = new { prompt = 100, completion = 20, total = 120 }
        };
    }

    private static async Task<string> HeartbeatAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            "/api/v1/agent/heartbeat",
            token,
            null,
            new
            {
                instanceName = $"agent-{Guid.NewGuid():N}"[..20],
                hostFingerprint = new string('a', 64),
                agentVersion = "0.1.0",
                mcpVersion = "0.1.0",
                runtimeLocation = "local",
                model = new
                {
                    name = ModelName,
                    version = ModelVersion,
                    quantization = ModelQuantization,
                    runtime = "local"
                },
                queueDepth = 0
            });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("instanceId").GetGuid().ToString();
    }

    private HttpClient CreateClient()
    {
        return environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? token,
        string? instanceId,
        object? body,
        string? sessionCookie = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            request.Headers.Add("X-Agent-Instance-Id", instanceId);
        }

        if (!string.IsNullOrWhiteSpace(sessionCookie))
        {
            request.Headers.Add("Cookie", sessionCookie);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return document.RootElement.GetProperty("code").GetString();
    }

    private static async Task EnableAiAsync(WhalePodDbContext db, User admin)
    {
        var settings = new AiSettingsService(db, new AdminAccessService(db), TimeProvider.System);
        var result = await settings.UpdateAsync(
            enabled: true,
            suggestionsPaused: false,
            approvedModels: [new ApprovedModel(ModelName, ModelVersion, ModelQuantization)],
            dailyTokenBudget: 100_000,
            confirmEnable: true,
            audit: ServiceTestSupport.NewAudit(admin.Id));
        Assert.True(result.Outcome is AiSettingsOutcome.Success or AiSettingsOutcome.Unchanged);
    }

    private static async Task SetPausedAsync(WhalePodDbContext db, User admin, bool paused)
    {
        var settings = new AiSettingsService(db, new AdminAccessService(db), TimeProvider.System);
        var result = await settings.UpdateAsync(
            enabled: true,
            suggestionsPaused: paused,
            approvedModels: [new ApprovedModel(ModelName, ModelVersion, ModelQuantization)],
            dailyTokenBudget: 100_000,
            confirmEnable: true,
            audit: ServiceTestSupport.NewAudit(admin.Id));
        Assert.True(result.Outcome is AiSettingsOutcome.Success or AiSettingsOutcome.Unchanged);
    }

    private static async Task DisableAiAsync(WhalePodDbContext db, User admin)
    {
        var settings = new AiSettingsService(db, new AdminAccessService(db), TimeProvider.System);
        await settings.UpdateAsync(
            enabled: false,
            suggestionsPaused: false,
            approvedModels: [],
            dailyTokenBudget: 100_000,
            confirmEnable: true,
            audit: ServiceTestSupport.NewAudit(admin.Id));
    }

    private async Task<AgentSetup> PrepareAsync(
        WhalePodDbContext db,
        bool enableAi,
        string body = "这是用于 Agent 审核任务的生命周期测试正文，长度足够且需要人工裁决。")
    {
        var now = DateTimeOffset.UtcNow;
        await db.Posts
            .Where(post => post.ModerationStatus == ModerationStatuses.Pending && post.DeletedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(post => post.DeletedAt, now));
        await db.Comments
            .Where(comment => comment.ModerationStatus == ModerationStatuses.Pending && comment.DeletedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(comment => comment.DeletedAt, now));
        await db.ModerationTasks
            .Where(task => task.Status == ModerationTaskStatuses.Queued
                || task.Status == ModerationTaskStatuses.Claimed
                || task.Status == ModerationTaskStatuses.Suggested
                || task.Status == ModerationTaskStatuses.Escalated)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(task => task.Status, ModerationTaskStatuses.Expired)
                .SetProperty(task => task.CompletedAt, now));
        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.System,
            Action = AiBudgetService.SuggestionSubmittedAction,
            TargetType = "agent_test",
            MetadataJson = JsonSerializer.SerializeToDocument(new { reason = "reset_failure_window" }),
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("agent-admin", UserRoles.Admin);
        var author = ServiceTestSupport.NewUser("agent-author");
        author.Handle = $"agent-author-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(
            author.Id,
            category.Id,
            ModerationStatuses.Pending,
            title: $"Agent 审核生命周期标题 {suffix}",
            body: body);
        db.Users.AddRange(admin, author);
        db.Categories.Add(category);
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        if (enableAi)
        {
            await EnableAiAsync(db, admin);
        }
        else
        {
            await DisableAiAsync(db, admin);
        }

        var token = await CreateTokenAsync(
            db,
            admin,
            $"agent-service-{suffix}",
            ["moderation:read", "moderation:suggest", "agent:heartbeat"]);
        return new AgentSetup(admin, author, post, token.AccountId, token.Token);
    }

    private static async Task<AgentTokenResult> CreateTokenAsync(
        WhalePodDbContext db,
        User admin,
        string name,
        string[] scopes,
        string[]? ipAllowlist = null)
    {
        var service = new ServiceAccountAdminService(
            db,
            new AdminAccessService(db),
            TimeProvider.System);
        var created = await service.CreateAsync(
            $"{name}-{ServiceTestSupport.NewSuffix()}",
            scopes,
            ipAllowlist,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(ServiceAccountOutcome.Success, created.Outcome);
        var issued = await service.IssueTokenAsync(
            created.AccountId!.Value,
            30,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(ServiceAccountOutcome.Success, issued.Outcome);
        return new AgentTokenResult(issued.Token!.PlaintextToken, created.AccountId.Value);
    }

    private sealed record AgentSetup(
        User Admin,
        User Author,
        Post Post,
        Guid AccountId,
        string Token);

    private sealed record AgentTokenResult(string Token, Guid AccountId);
}