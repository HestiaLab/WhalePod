using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Communications;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public enum SettingsUpdateResult
{
    Success,
    Unchanged,
    InvalidInput,
    NotFound
}

public sealed record ProfileEditData(
    Guid UserId,
    string Handle,
    string DisplayName,
    string? Bio,
    string? AvatarUrl,
    short TrustLevel);

public sealed record PublicProfileData(
    string Handle,
    string DisplayName,
    string? Bio,
    string? AvatarUrl,
    short TrustLevel,
    DateTimeOffset CreatedAt);

public sealed record EmailPreferenceData(
    bool RepliesMentions,
    bool ModerationResults,
    bool AccountSecurity);

public sealed class ProfileSettingsService(WhalePodDbContext db, TimeProvider timeProvider)
{
    public async Task<ProfileEditData?> GetProfileAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id == userId
                && (candidate.Status == UserStatuses.Active || candidate.Status == UserStatuses.Muted)
                && candidate.ActivatedAt != null
                && candidate.DeletedAt == null)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Handle,
                candidate.DisplayName,
                candidate.Bio,
                candidate.AvatarUrl,
                candidate.TrustLevel
            })
            .SingleOrDefaultAsync(cancellationToken);

        return user is null
            ? null
            : new ProfileEditData(
                user.Id,
                user.Handle,
                user.DisplayName,
                user.Bio,
                user.AvatarUrl,
                user.TrustLevel);
    }

    public async Task<PublicProfileData?> GetPublicProfileAsync(
        string? handle,
        CancellationToken cancellationToken = default)
    {
        var normalizedHandle = (handle ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedHandle.Length is < 3 or > 32)
        {
            return null;
        }

        var user = await db.Users
            .AsNoTracking()
            .Where(candidate => candidate.Handle == normalizedHandle
                && (candidate.Status == UserStatuses.Active || candidate.Status == UserStatuses.Muted)
                && candidate.ActivatedAt != null
                && candidate.DeletedAt == null)
            .Select(candidate => new
            {
                candidate.Handle,
                candidate.DisplayName,
                candidate.Bio,
                candidate.AvatarUrl,
                candidate.TrustLevel,
                candidate.CreatedAt
            })
            .SingleOrDefaultAsync(cancellationToken);

        return user is null
            ? null
            : new PublicProfileData(
                user.Handle,
                user.DisplayName,
                user.Bio,
                user.AvatarUrl,
                user.TrustLevel,
                user.CreatedAt);
    }

    public async Task<SettingsUpdateResult> UpdateProfileAsync(
        Guid userId,
        string? displayName,
        string? bio,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var user = await LoadEditableUserAsync(userId, cancellationToken);
        if (user is null)
        {
            return SettingsUpdateResult.NotFound;
        }

        var normalizedDisplayName = (displayName ?? string.Empty).Trim();
        if (normalizedDisplayName.Length == 0
            || normalizedDisplayName.EnumerateRunes().Count() > 80
            || normalizedDisplayName.Any(char.IsControl))
        {
            return SettingsUpdateResult.InvalidInput;
        }

        var normalizedBio = string.IsNullOrWhiteSpace(bio) ? null : bio.Trim();
        if (normalizedBio is not null
            && (normalizedBio.EnumerateRunes().Count() > 500
                || normalizedBio.Any(character => char.IsControl(character)
                    && character is not '\r' and not '\n' and not '\t')))
        {
            return SettingsUpdateResult.InvalidInput;
        }

        var displayNameChanged = !string.Equals(user.DisplayName, normalizedDisplayName, StringComparison.Ordinal);
        var bioChanged = !string.Equals(user.Bio, normalizedBio, StringComparison.Ordinal);
        if (!displayNameChanged && !bioChanged)
        {
            return SettingsUpdateResult.Unchanged;
        }

        var now = timeProvider.GetUtcNow();
        user.DisplayName = normalizedDisplayName;
        user.Bio = normalizedBio;
        user.UpdatedAt = now;

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = userId,
            Action = "profile.updated",
            TargetType = "user",
            TargetId = userId,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(new
            {
                displayNameChanged,
                bioChanged
            }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        return SettingsUpdateResult.Success;
    }

    public async Task<EmailPreferenceData?> GetEmailPreferencesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var userExists = await db.Users
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == userId
                && (candidate.Status == UserStatuses.Active || candidate.Status == UserStatuses.Muted)
                && candidate.ActivatedAt != null
                && candidate.DeletedAt == null, cancellationToken);

        if (!userExists)
        {
            return null;
        }

        var preferences = await db.EmailPreferences
            .AsNoTracking()
            .Where(preference => preference.UserId == userId)
            .ToDictionaryAsync(preference => preference.Type, preference => preference.Enabled, cancellationToken);

        return new EmailPreferenceData(
            preferences.GetValueOrDefault(EmailPreferenceTypes.RepliesMentions, true),
            preferences.GetValueOrDefault(EmailPreferenceTypes.ModerationResults, true),
            true);
    }

    public async Task<SettingsUpdateResult> UpdateEmailPreferencesAsync(
        Guid userId,
        bool repliesMentions,
        bool moderationResults,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var user = await LoadEditableUserAsync(userId, cancellationToken);
        if (user is null)
        {
            return SettingsUpdateResult.NotFound;
        }

        var now = timeProvider.GetUtcNow();
        var existing = await db.EmailPreferences
            .Where(preference => preference.UserId == userId)
            .ToDictionaryAsync(preference => preference.Type, cancellationToken);

        var changed = false;
        changed |= ApplyPreference(
            existing,
            EmailPreferenceTypes.RepliesMentions,
            repliesMentions,
            userId,
            now);
        changed |= ApplyPreference(
            existing,
            EmailPreferenceTypes.ModerationResults,
            moderationResults,
            userId,
            now);
        changed |= ApplyPreference(
            existing,
            EmailPreferenceTypes.AccountSecurity,
            true,
            userId,
            now);

        if (!changed)
        {
            return SettingsUpdateResult.Unchanged;
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = userId,
            Action = "email_pref.updated",
            TargetType = "user",
            TargetId = userId,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(new
            {
                repliesMentions,
                moderationResults,
                accountSecurity = true
            }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        return SettingsUpdateResult.Success;
    }

    private async Task<User?> LoadEditableUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await db.Users.SingleOrDefaultAsync(
            user => user.Id == userId
                && (user.Status == UserStatuses.Active || user.Status == UserStatuses.Muted)
                && user.ActivatedAt != null
                && user.DeletedAt == null,
            cancellationToken);
    }

    private bool ApplyPreference(
        Dictionary<string, EmailPreference> existing,
        string type,
        bool enabled,
        Guid userId,
        DateTimeOffset now)
    {
        if (existing.TryGetValue(type, out var preference))
        {
            if (preference.Enabled == enabled)
            {
                return false;
            }

            preference.Enabled = enabled;
            preference.UpdatedAt = now;
            return true;
        }

        var created = new EmailPreference
        {
            UserId = userId,
            Type = type,
            Enabled = enabled,
            UpdatedAt = now
        };
        db.EmailPreferences.Add(created);
        existing[type] = created;
        return true;
    }
}