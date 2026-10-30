using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class AuditAdminServiceTests
{
    private static WhalePodDbContext CreateContext() => ServiceTestSupport.CreateContext();

    [DatabaseFact]
    public async Task List_filters_by_actor_action_target_and_time()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var actor = NewUser("audit-actor");
        var otherActor = NewUser("audit-other");
        var admin = ServiceTestSupport.NewUser("audit-admin", UserRoles.Admin);
        var now = DateTimeOffset.UtcNow;

        db.Users.AddRange(actor, otherActor, admin);
        db.AuditLogs.AddRange(
            NewAudit(
                AuditActorTypes.User,
                actor.Id,
                $"audit-{suffix}.profile",
                "user",
                actor.Id,
                new { safe = "one" },
                now.AddHours(-1)),
            NewAudit(
                AuditActorTypes.User,
                otherActor.Id,
                $"audit-{suffix}.invite",
                "invite_code",
                Guid.NewGuid(),
                new { safe = "two" },
                now.AddHours(-2)),
            NewAudit(
                AuditActorTypes.System,
                null,
                $"audit-{suffix}.cleanup",
                null,
                null,
                new { safe = "three" },
                now.AddHours(-3)));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var filter = new AuditFilter(
            actor.Handle,
            $"audit-{suffix}.profile",
            "user",
            actor.Id,
            now.AddHours(-4),
            now.AddHours(1));

        var result = await service.ListAsync(admin.Id, filter, 1);
        var item = Assert.Single(result.Items);
        Assert.Equal(actor.Id, item.ActorId);
        Assert.Equal(actor.Handle, item.ActorHandle);
        Assert.Equal($"audit-{suffix}.profile", item.Action);
        Assert.Equal("user", item.TargetType);
        Assert.Equal(actor.Id, item.TargetId);
        Assert.Equal(1, result.TotalCount);

        var byTime = await service.ListAsync(
            admin.Id,
            new AuditFilter(null, $"audit-{suffix}.invite", null, null, now.AddHours(-2.5), now.AddHours(-1.5)),
            1);
        Assert.Single(byTime.Items);

        db.ChangeTracker.Clear();
        var detail = Assert.IsType<AuditDetailResult>(
            await service.GetDetailAsync((await db.AuditLogs
                .SingleAsync(log => log.Action == $"audit-{suffix}.profile")).Id));
        Assert.Contains("one", detail.MetadataJson);
        Assert.Null(await service.GetDetailAsync(Guid.NewGuid()));
    }

    [DatabaseFact]
    public async Task Detail_redacts_sensitive_metadata_keys()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var actor = NewUser("audit-redact");
        var log = NewAudit(
            AuditActorTypes.User,
            actor.Id,
            $"audit-{suffix}.redact",
            "user",
            actor.Id,
            new
            {
                safe = "visible",
                email = "person@example.com",
                ipHash = "raw-ip-hash",
                nested = new { token = "secret-token", value = "ok" },
                items = new[] { new { password = "secret-password" } }
            },
            DateTimeOffset.UtcNow);

        db.Users.Add(actor);
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync();

        var detail = Assert.IsType<AuditDetailResult>(
            await CreateService(db).GetDetailAsync(log.Id));

        Assert.Contains("visible", detail.MetadataJson);
        Assert.Contains("ok", detail.MetadataJson);
        Assert.Contains("[redacted]", detail.MetadataJson);
        Assert.DoesNotContain("person@example.com", detail.MetadataJson);
        Assert.DoesNotContain("raw-ip-hash", detail.MetadataJson);
        Assert.DoesNotContain("secret-token", detail.MetadataJson);
        Assert.DoesNotContain("secret-password", detail.MetadataJson);
    }

    [DatabaseFact]
    public async Task Export_streams_redacted_rows_and_records_export_audit()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var actor = NewUser("audit-export");
        var now = DateTimeOffset.UtcNow;
        var actionPrefix = $"audit-{suffix}.export";

        db.Users.Add(actor);
        db.AuditLogs.AddRange(
            NewAudit(
                AuditActorTypes.User,
                actor.Id,
                actionPrefix,
                "user",
                actor.Id,
                new { order = 1, token = "first-secret" },
                now.AddMinutes(-3)),
            NewAudit(
                AuditActorTypes.System,
                null,
                actionPrefix,
                "post",
                Guid.NewGuid(),
                new { order = 2, value = "visible" },
                now.AddMinutes(-2)),
            NewAudit(
                AuditActorTypes.User,
                actor.Id,
                actionPrefix,
                "user",
                actor.Id,
                new { order = 3, email = "second@example.com" },
                now.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var filter = new AuditFilter(
            null,
            actionPrefix,
            null,
            null,
            now.AddHours(-1),
            now.AddHours(1));

        var service = CreateService(db);
        var rows = new List<AuditExportRow>();
        await foreach (var row in service.ExportAsync(filter))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].CreatedAt < rows[1].CreatedAt);
        Assert.DoesNotContain("first-secret", rows[0].MetadataJson);
        Assert.Contains("visible", rows[1].MetadataJson);
        Assert.DoesNotContain("second@example.com", rows[2].MetadataJson);

        Assert.True(AuditAdminService.IsExportRangeValid(filter));
        Assert.False(AuditAdminService.IsExportRangeValid(
            filter with { ToExclusive = filter.From.AddDays(32) }));
        Assert.False(AuditAdminService.IsExportRangeValid(
            filter with { ToExclusive = filter.From.AddMinutes(-1) }));

        await service.RecordExportAsync(actor.Id, filter, rows.Count, NewAuditContext(actor.Id));

        db.ChangeTracker.Clear();
        var exportAudit = await db.AuditLogs.SingleAsync(log =>
            log.ActorId == actor.Id && log.Action == "audit.exported");
        Assert.Equal(3, exportAudit.MetadataJson.RootElement.GetProperty("rows").GetInt32());
        Assert.Equal(32, exportAudit.IpHash?.Length);
    }

    private static AuditAdminService CreateService(WhalePodDbContext db)
    {
        return new AuditAdminService(db, new AdminAccessService(db), TimeProvider.System);
    }

    private static AuditContext NewAuditContext(Guid actorId)
    {
        return new AuditContext(actorId, "test-request", new byte[32]);
    }

    private static User NewUser(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return new User
        {
            Handle = $"{prefix}-{suffix}",
            DisplayName = "测试用户",
            Status = UserStatuses.Active,
            Role = UserRoles.Member,
            ActivatedAt = DateTimeOffset.UtcNow
        };
    }

    private static AuditLog NewAudit(
        string actorType,
        Guid? actorId,
        string action,
        string? targetType,
        Guid? targetId,
        object metadata,
        DateTimeOffset createdAt)
    {
        return new AuditLog
        {
            ActorType = actorType,
            ActorId = actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            RequestId = "test-request",
            IpHash = new byte[32],
            MetadataJson = JsonSerializer.SerializeToDocument(metadata),
            CreatedAt = createdAt
        };
    }
}