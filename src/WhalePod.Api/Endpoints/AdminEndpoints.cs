using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.RateLimiting;

using WhalePod.Domain;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Endpoints;

internal static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var reads = app.MapGroup("/api/v1/admin").RequireAuthorization();
        var writes = app.MapGroup("/api/v1/admin")
            .RequireAuthorization()
            .AddEndpointFilter<CsrfValidationFilter>()
            .RequireRateLimiting("api-write");

        reads.MapGet("/moderation/queue", async (
            string? targetType,
            string? limit,
            string? cursor,
            HttpContext http,
            ContentModerationService moderation) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var page = await moderation.GetQueueAsync(
                actorId,
                targetType,
                EndpointHelpers.ParseLimit(limit),
                cursor,
                http.RequestAborted);
            if (page.InvalidCursor)
            {
                throw ApiProblemException.Validation("cursor", "cursor 无效或与当前筛选不匹配。");
            }

            return Results.Ok(new { data = new { items = page.Items, nextCursor = page.NextCursor } });
        });

        writes.MapPost("/moderation/{targetType}/{targetId}/action", async (
            string targetType,
            string targetId,
            HttpContext http,
            ContentModerationService moderation,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(targetId, out var parsedTargetId))
            {
                throw ApiProblemException.NotFound("待审核内容不存在或已删除。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var command = await EndpointHelpers.ReadJsonAsync<ModerationActionCommand>(http);
            var result = await moderation.ExecuteAsync(
                actorId,
                targetType,
                parsedTargetId,
                command,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = result });
        });

        reads.MapGet("/reports", async (
            string? status,
            string? limit,
            string? cursor,
            HttpContext http,
            ReportService reports) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var page = await reports.ListAsync(
                actorId,
                status,
                EndpointHelpers.ParseLimit(limit),
                cursor,
                http.RequestAborted);
            if (page.InvalidCursor)
            {
                throw ApiProblemException.Validation("cursor", "cursor 无效或与当前筛选不匹配。");
            }

            return Results.Ok(new { data = new { items = page.Items, nextCursor = page.NextCursor } });
        });

        writes.MapPost("/reports/{reportId}/resolve", async (
            string reportId,
            HttpContext http,
            ReportService reports,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(reportId, out var parsedReportId))
            {
                throw ApiProblemException.NotFound("举报不存在。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var command = await EndpointHelpers.ReadJsonAsync<ReportResolveCommand>(http);
            var report = await reports.ResolveAsync(
                actorId,
                parsedReportId,
                command,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = report });
        });

        reads.MapGet("/users", async (
            string? query,
            string? status,
            string? trustLevel,
            string? limit,
            string? cursor,
            HttpContext http,
            AdminUserService users) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var page = await users.ListAsync(
                actorId,
                query,
                status,
                EndpointHelpers.ParseTrustLevel(trustLevel),
                EndpointHelpers.ParseLimit(limit),
                cursor,
                http.RequestAborted);
            if (page.InvalidCursor)
            {
                throw ApiProblemException.Validation("cursor", "cursor 无效或与当前筛选不匹配。");
            }

            return Results.Ok(new { data = new { items = page.Items, nextCursor = page.NextCursor } });
        });

        reads.MapGet("/users/{userId}", async (
            string userId,
            HttpContext http,
            AdminUserService users) =>
        {
            if (!Guid.TryParse(userId, out var parsedUserId))
            {
                throw ApiProblemException.NotFound("用户不存在。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var user = await users.GetAsync(actorId, parsedUserId, http.RequestAborted);
            return Results.Ok(new { data = user });
        });

        writes.MapPatch("/users/{userId}", async (
            string userId,
            HttpContext http,
            AdminUserService users,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(userId, out var parsedUserId))
            {
                throw ApiProblemException.NotFound("用户不存在。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var request = await EndpointHelpers.ReadJsonAsync<AdminTrustLevelRequest>(http);
            if (request.TrustLevel is null or < 0 or > 4)
            {
                throw ApiProblemException.Validation("trustLevel", "trustLevel 必须在 0–4 之间。");
            }

            var user = await users.ChangeTrustLevelAsync(
                actorId,
                parsedUserId,
                (short)request.TrustLevel.Value,
                request.Reason,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = user });
        });

        writes.MapPost("/users/{userId}/status", async (
            string userId,
            HttpContext http,
            AdminUserService users,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(userId, out var parsedUserId))
            {
                throw ApiProblemException.NotFound("用户不存在。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var request = await EndpointHelpers.ReadJsonAsync<AdminUserStatusRequest>(http);
            var user = await users.ChangeStatusAsync(
                actorId,
                parsedUserId,
                request.Status,
                request.Reason,
                request.ExpiresAt,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = user });
        });

        reads.MapGet("/invites", async (
            string? status,
            string? limit,
            string? cursor,
            HttpContext http,
            InviteAdminService invites) =>
        {
            if (!string.IsNullOrWhiteSpace(status)
                && status is not ("active" or "disabled"))
            {
                throw ApiProblemException.Validation("status", "status 必须是 active 或 disabled。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var pageSize = EndpointHelpers.ParseLimit(limit);
            var page = ParseAdminPage(cursor);
            var result = await invites.ListAsync(
                actorId,
                status,
                null,
                null,
                page,
                pageSize,
                http.RequestAborted);
            return Results.Ok(new
            {
                data = new
                {
                    items = result.Items.Select(ToInviteApiView).ToList(),
                    nextCursor = NextPageCursor(page, result.PageSize, result.TotalCount)
                }
            });
        });

        writes.MapPost("/invites", async (
            HttpContext http,
            InviteAdminService invites,
            ApiAuditContextFactory auditFactory) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var request = await EndpointHelpers.ReadJsonAsync<InviteCreateApiRequest>(http);
            var result = await invites.CreateAsync(
                request.Note,
                request.MaxUses ?? 1,
                request.ExpiresAt,
                auditFactory.Create(actorId),
                http.RequestAborted);
            if (result.Outcome != InviteAdminOutcome.Success || result.Invite is null)
            {
                throw MapInviteOutcome(result.Outcome);
            }

            var invite = result.Invite;
            return Results.Json(
                new
                {
                    data = new
                    {
                        invite.Id,
                        invite.CodeHint,
                        invite.CreatedByHandle,
                        invite.MaxUses,
                        invite.UsedCount,
                        invite.ExpiresAt,
                        invite.Status,
                        invite.State,
                        invite.Note,
                        invite.CreatedAt,
                        disabledAt = (DateTimeOffset?)null,
                        invite.PlaintextCode
                    }
                },
                statusCode: StatusCodes.Status201Created);
        });

        writes.MapPost("/invites/{inviteId:guid}/disable", async (
            Guid inviteId,
            HttpContext http,
            InviteAdminService invites,
            ApiAuditContextFactory auditFactory) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var result = await invites.DisableUnusedAsync(
                [inviteId],
                auditFactory.Create(actorId),
                http.RequestAborted);
            if (result.Outcome != InviteAdminOutcome.Success)
            {
                throw MapInviteOutcome(result.Outcome);
            }

            var detail = await invites.GetDetailAsync(inviteId, 1, http.RequestAborted);
            if (detail is null)
            {
                throw ApiProblemException.NotFound("邀请码不存在。");
            }

            return Results.Ok(new { data = ToInviteApiView(detail.Invite) });
        });

        reads.MapGet("/categories", async (
            HttpContext http,
            CategoryAdminService categories) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var items = await categories.ListAsync(actorId, http.RequestAborted);
            return Results.Ok(new
            {
                data = new
                {
                    items = items.Select(ToCategoryApiView).ToList()
                }
            });
        });

        writes.MapPost("/categories", async (
            HttpContext http,
            CategoryAdminService categories,
            ApiAuditContextFactory auditFactory) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var request = await EndpointHelpers.ReadJsonAsync<CategoryCreateApiRequest>(http);
            var result = await categories.CreateAsync(
                actorId,
                request.Slug,
                request.Name,
                request.Description,
                request.DisplayOrder ?? 0,
                requiresKernel: false,
                auditFactory.Create(actorId),
                http.RequestAborted);
            if (result.Outcome != CategoryMutationOutcome.Success
                || result.CategoryId is null)
            {
                throw MapCategoryOutcome(result);
            }

            var created = await categories.GetAsync(
                actorId,
                result.CategoryId.Value,
                http.RequestAborted);
            if (created is null)
            {
                throw ApiProblemException.NotFound("板块不存在。");
            }

            return Results.Json(
                new { data = ToCategoryApiView(created) },
                statusCode: StatusCodes.Status201Created);
        });

        writes.MapPatch("/categories/{categoryId:guid}", async (
            Guid categoryId,
            HttpContext http,
            CategoryAdminService categories,
            ApiAuditContextFactory auditFactory) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var body = await ReadJsonElementAsync(http);
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in body.EnumerateObject())
            {
                properties.Add(property.Name);
            }

            if (properties.Count == 0 || properties.Any(name => name is not
                ("name" or "description" or "displayOrder" or "status")))
            {
                throw ApiProblemException.Validation(
                    "body",
                    "至少提供一个可更新字段：name、description、displayOrder、status。");
            }

            var current = await categories.GetAsync(actorId, categoryId, http.RequestAborted)
                ?? throw ApiProblemException.NotFound("板块不存在。");
            var audit = auditFactory.Create(actorId);

            if (properties.Any(name => name is "name" or "description" or "displayOrder"))
            {
                var name = properties.Contains("name")
                    ? ReadRequiredString(body, "name")
                    : current.Name;
                var description = properties.Contains("description")
                    ? ReadNullableString(body, "description")
                    : current.Description;
                var displayOrder = properties.Contains("displayOrder")
                    ? ReadRequiredInt(body, "displayOrder")
                    : current.DisplayOrder;
                var update = await categories.UpdateAsync(
                    actorId,
                    categoryId,
                    name,
                    description,
                    displayOrder,
                    current.RequiresKernel,
                    audit,
                    http.RequestAborted);
                if (update.Outcome is not (CategoryMutationOutcome.Success
                    or CategoryMutationOutcome.Unchanged))
                {
                    throw MapCategoryOutcome(update);
                }
            }

            if (properties.Contains("status"))
            {
                var status = ReadRequiredString(body, "status");
                if (status is not ("active" or "disabled"))
                {
                    throw ApiProblemException.Validation(
                        "status",
                        "status 必须是 active 或 disabled。");
                }

                var update = await categories.SetStatusAsync(
                    actorId,
                    categoryId,
                    disabled: status == "disabled",
                    confirmImpact: true,
                    audit,
                    http.RequestAborted);
                if (update.Outcome is not (CategoryMutationOutcome.Success
                    or CategoryMutationOutcome.Unchanged))
                {
                    throw MapCategoryOutcome(update);
                }
            }

            var updated = await categories.GetAsync(actorId, categoryId, http.RequestAborted)
                ?? throw ApiProblemException.NotFound("板块不存在。");
            return Results.Ok(new { data = ToCategoryApiView(updated) });
        });

        reads.MapGet("/audit", async (
            string? actorType,
            string? actorId,
            string? action,
            string? targetType,
            string? targetId,
            string? from,
            string? to,
            string? limit,
            string? cursor,
            HttpContext http,
            AuditAdminService audit) =>
        {
            if (!string.IsNullOrWhiteSpace(actorType)
                && actorType is not (AuditActorTypes.User
                    or AuditActorTypes.Service
                    or AuditActorTypes.System))
            {
                throw ApiProblemException.Validation(
                    "actorType",
                    "actorType 必须是 user、service 或 system。");
            }

            if (action?.Length > 100 || targetType?.Length > 40)
            {
                throw ApiProblemException.Validation("query", "筛选参数过长。");
            }

            var now = DateTimeOffset.UtcNow;
            var fromValue = ParseDateTimeOffset(from, "from") ?? now.AddDays(-7);
            var toValue = ParseDateTimeOffset(to, "to") ?? now;
            if (toValue <= fromValue)
            {
                throw ApiProblemException.Validation("to", "to 必须晚于 from。");
            }

            var actorIdValue = ParseOptionalGuid(actorId, "actorId");
            var targetIdValue = ParseOptionalGuid(targetId, "targetId");
            var actor = EndpointHelpers.RequireActorId(http);
            var pageSize = EndpointHelpers.ParseLimit(limit);
            var page = ParseAdminPage(cursor);
            var result = await audit.ListAsync(
                actor,
                new AuditFilter(
                    null,
                    string.IsNullOrWhiteSpace(action) ? null : action.Trim(),
                    string.IsNullOrWhiteSpace(targetType) ? null : targetType.Trim(),
                    targetIdValue,
                    fromValue,
                    toValue,
                    string.IsNullOrWhiteSpace(actorType) ? null : actorType.Trim(),
                    actorIdValue),
                page,
                pageSize,
                http.RequestAborted);
            return Results.Ok(new
            {
                data = new
                {
                    items = result.Items.Select(item => new AuditApiView(
                        item.Id,
                        item.ActorType,
                        item.ActorId,
                        item.ActorHandle,
                        item.Action,
                        item.TargetType,
                        item.TargetId,
                        item.CreatedAt)).ToList(),
                    nextCursor = NextPageCursor(page, result.PageSize, result.TotalCount)
                }
            });
        });
    }

    private static InviteApiView ToInviteApiView(InviteAdminItem item)
    {
        return new InviteApiView(
            item.Id,
            item.CodeHint,
            item.CreatedByHandle,
            item.MaxUses,
            item.UsedCount,
            item.ExpiresAt,
            item.Status,
            item.State,
            item.Note,
            item.CreatedAt,
            item.DisabledAt);
    }

    private static CategoryApiView ToCategoryApiView(CategoryAdminItem item)
    {
        return new CategoryApiView(
            item.Id,
            item.Slug,
            item.Name,
            item.Description,
            item.DisplayOrder,
            item.Status);
    }

    private static ApiProblemException MapInviteOutcome(InviteAdminOutcome outcome)
    {
        return outcome switch
        {
            InviteAdminOutcome.Forbidden => ApiProblemException.Forbidden(),
            InviteAdminOutcome.InvalidInput => ApiProblemException.Validation(
                "request",
                "邀请码参数无效，请检查次数、备注与有效期。"),
            InviteAdminOutcome.NoSelection => ApiProblemException.Validation(
                "inviteId",
                "请至少选择一个邀请码。"),
            _ => ApiProblemException.Validation("request", "邀请码操作未成功。")
        };
    }

    private static ApiProblemException MapCategoryOutcome(CategoryMutationResult result)
    {
        return result.Outcome switch
        {
            CategoryMutationOutcome.InvalidInput => ApiProblemException.Validation(
                result.Field ?? "request",
                "板块参数不符合要求。"),
            CategoryMutationOutcome.DuplicateSlug => ApiProblemException.Conflict(
                "板块 slug 已存在。",
                new { field = "slug" }),
            CategoryMutationOutcome.NotFound => ApiProblemException.NotFound("板块不存在。"),
            CategoryMutationOutcome.ConfirmationRequired => ApiProblemException.Conflict(
                "停用板块需要确认影响范围。"),
            _ => ApiProblemException.Validation("request", "板块操作未成功。")
        };
    }

    private static int ParseAdminPage(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 1;
        }

        if (cursor.Length > 512)
        {
            throw ApiProblemException.Validation("cursor", "cursor 过长。");
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            if (!text.StartsWith("page:", StringComparison.Ordinal)
                || !int.TryParse(
                    text["page:".Length..],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var page)
                || page < 1)
            {
                throw ApiProblemException.Validation("cursor", "cursor 无效。");
            }

            return page;
        }
        catch (FormatException)
        {
            throw ApiProblemException.Validation("cursor", "cursor 无效。");
        }
    }

    private static string? NextPageCursor(int page, int pageSize, int totalCount)
    {
        return page * pageSize < totalCount ? EncodeAdminPage(page + 1) : null;
    }

    private static string EncodeAdminPage(int page)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"page:{page}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static async Task<JsonElement> ReadJsonElementAsync(HttpContext http)
    {
        if (!http.Request.HasJsonContentType())
        {
            throw ApiProblemException.Validation("body", "请求体必须是 JSON。");
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(
                http.Request.Body,
                cancellationToken: http.RequestAborted);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw ApiProblemException.Validation("body", "请求体不是合法 JSON。");
        }
    }

    private static string ReadRequiredString(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw ApiProblemException.Validation(name, $"{name} 必须是非空字符串。");
        }

        return value.GetString()!;
    }

    private static string? ReadNullableString(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw ApiProblemException.Validation(name, $"{name} 必须是字符串或 null。");
        }

        return value.GetString();
    }

    private static int ReadRequiredInt(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)
            || !value.TryGetInt32(out var number))
        {
            throw ApiProblemException.Validation(name, $"{name} 必须是整数。");
        }

        return number;
    }

    private static Guid? ParseOptionalGuid(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Guid.TryParse(value, out var parsed)
            ? parsed
            : throw ApiProblemException.Validation(field, $"{field} 必须是 UUID。");
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : throw ApiProblemException.Validation(field, $"{field} 必须是 ISO 8601 时间。");
    }

    private sealed record InviteApiView(
        Guid Id,
        string? CodeHint,
        string CreatedBy,
        int MaxUses,
        int UsedCount,
        DateTimeOffset? ExpiresAt,
        string Status,
        string State,
        string? Note,
        DateTimeOffset CreatedAt,
        DateTimeOffset? DisabledAt);

    private sealed record CategoryApiView(
        Guid Id,
        string Slug,
        string Name,
        string? Description,
        int DisplayOrder,
        string Status);

    private sealed record AuditApiView(
        Guid Id,
        string ActorType,
        Guid? ActorId,
        string? ActorHandle,
        string Action,
        string? TargetType,
        Guid? TargetId,
        DateTimeOffset CreatedAt);

    private sealed record InviteCreateApiRequest(
        string? Note,
        int? MaxUses,
        DateTimeOffset? ExpiresAt);

    private sealed record CategoryCreateApiRequest(
        string? Slug,
        string? Name,
        string? Description,
        int? DisplayOrder);

    private sealed record AdminTrustLevelRequest(int? TrustLevel, string? Reason);

    private sealed record AdminUserStatusRequest(
        string? Status,
        string? Reason,
        DateTimeOffset? ExpiresAt);
}