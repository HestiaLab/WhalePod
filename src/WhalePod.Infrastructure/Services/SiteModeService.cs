using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record SiteModeData(
    bool ReadOnly,
    bool LoginDisabled,
    bool ReadOnlyAllowMemberWrite,
    DateTimeOffset? UpdatedAt);

public sealed class SiteModeService(
    WhalePodDbContext db,
    AdminAccessService adminAccess,
    TimeProvider timeProvider)
{
    private const string ReadOnlyKey = "site.read_only";
    private const string LoginDisabledKey = "site.login_disabled";
    private const string ReadOnlyAllowMemberWriteKey = "site.read_only_allow_member_write";

    public async Task<SiteModeData> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await db.SiteSettings
            .AsNoTracking()
            .Where(setting => setting.Key == ReadOnlyKey
                || setting.Key == LoginDisabledKey
                || setting.Key == ReadOnlyAllowMemberWriteKey)
            .ToDictionaryAsync(setting => setting.Key, setting => setting, cancellationToken);
        return new SiteModeData(
            ReadBool(settings, ReadOnlyKey),
            ReadBool(settings, LoginDisabledKey),
            ReadBool(settings, ReadOnlyAllowMemberWriteKey),
            settings.Values
                .OrderByDescending(setting => setting.UpdatedAt)
                .Select(setting => (DateTimeOffset?)setting.UpdatedAt)
                .FirstOrDefault());
    }

    public async Task<bool> IsReadOnlyAsync(CancellationToken cancellationToken = default)
    {
        return await ReadSettingAsync(ReadOnlyKey, cancellationToken);
    }

    public async Task<bool> IsLoginDisabledAsync(CancellationToken cancellationToken = default)
    {
        return await ReadSettingAsync(LoginDisabledKey, cancellationToken);
    }

    public Task<SiteModeData> UpdateAsync(
        Guid actorId,
        bool readOnly,
        bool loginDisabled,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(
            actorId,
            readOnly,
            loginDisabled,
            readOnlyAllowMemberWrite: false,
            audit,
            cancellationToken);
    }

    public async Task<SiteModeData> UpdateAsync(
        Guid actorId,
        bool readOnly,
        bool loginDisabled,
        bool readOnlyAllowMemberWrite,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(actorId, cancellationToken))
        {
            throw ApiProblemException.Forbidden();
        }

        var current = await GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (current.ReadOnly != readOnly)
        {
            SetSetting(ReadOnlyKey, readOnly, actorId, now);
            db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                audit,
                "site.read_only_changed",
                "site_setting",
                null,
                new { readOnly },
                now));
        }

        if (current.LoginDisabled != loginDisabled)
        {
            SetSetting(LoginDisabledKey, loginDisabled, actorId, now);
            db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                audit,
                "site.login_disabled_changed",
                "site_setting",
                null,
                new { loginDisabled },
                now));
        }

        if (current.ReadOnlyAllowMemberWrite != readOnlyAllowMemberWrite)
        {
            SetSetting(
                ReadOnlyAllowMemberWriteKey,
                readOnlyAllowMemberWrite,
                actorId,
                now);
            db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                audit,
                "site.read_only_allow_member_write_changed",
                "site_setting",
                null,
                new { readOnlyAllowMemberWrite },
                now));
        }

        if (current.ReadOnly == readOnly
            && current.LoginDisabled == loginDisabled
            && current.ReadOnlyAllowMemberWrite == readOnlyAllowMemberWrite)
        {
            return current;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new SiteModeData(readOnly, loginDisabled, readOnlyAllowMemberWrite, now);
    }

    private async Task<bool> ReadSettingAsync(string key, CancellationToken cancellationToken)
    {
        var value = await db.SiteSettings
            .AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken);
        return value is not null
            && value.RootElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.RootElement.GetBoolean();
    }

    private void SetSetting(string key, bool value, Guid actorId, DateTimeOffset now)
    {
        var setting = db.SiteSettings.SingleOrDefault(candidate => candidate.Key == key);
        if (setting is null)
        {
            setting = new SiteSetting
            {
                Key = key,
                Value = JsonSerializer.SerializeToDocument(value),
                UpdatedAt = now,
                UpdatedBy = actorId
            };
            db.SiteSettings.Add(setting);
            return;
        }

        setting.Value = JsonSerializer.SerializeToDocument(value);
        setting.UpdatedAt = now;
        setting.UpdatedBy = actorId;
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, SiteSetting> settings,
        string key)
    {
        return settings.TryGetValue(key, out var setting)
            && setting.Value.RootElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && setting.Value.RootElement.GetBoolean();
    }
}