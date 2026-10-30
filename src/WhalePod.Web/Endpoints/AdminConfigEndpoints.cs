using System.Globalization;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Antiforgery;

using WhalePod.Domain;
using WhalePod.Infrastructure.Services;
using WhalePod.Web.Security;
using WhalePod.Web.Services;

namespace WhalePod.Web.Endpoints;

internal static class AdminConfigEndpoints
{
    public static void MapAdminConfigEndpoints(this WebApplication app)
    {
        app.MapPost("/admin/tags/{tagId:guid}/rename", async (
            Guid tagId,
            HttpContext http,
            IAntiforgery antiforgery,
            TagAdminService tagAdmin,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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

        app.MapPost("/admin/audit/export", async (
            HttpContext http,
            IAntiforgery antiforgery,
            AuditAdminService auditAdmin,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
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
}