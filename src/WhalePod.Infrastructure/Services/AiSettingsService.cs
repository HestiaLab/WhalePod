using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public enum AiSettingsOutcome
{
    Success,
    Unchanged,
    InvalidInput,
    ConfirmationRequired,
    Forbidden
}

public sealed record ApprovedModel(string Name, string Version, string Quantization);

public sealed record AiSettingsData(
    bool Enabled,
    bool SuggestionsPaused,
    IReadOnlyList<ApprovedModel> ApprovedModels,
    int DailyTokenBudget,
    int DailyTaskBudget,
    int DailyContentBudget,
    int QualityMinDecided,
    decimal QualityMinAgreement);

public sealed record AiSettingsUpdateResult(
    AiSettingsOutcome Outcome,
    string? ErrorCode = null);

public sealed class AiSettingsService(
    WhalePodDbContext db,
    AdminAccessService adminAccess,
    TimeProvider timeProvider)
{
    public const int DefaultDailyTokenBudget = 2_000_000;
    public const int MaxDailyTokenBudget = 1_000_000_000;
    public const int DefaultDailyTaskBudget = 10_000;
    public const int DefaultDailyContentBudget = 10_000;
    public const int MaxDailyTaskBudget = 1_000_000;
    public const int MaxDailyContentBudget = 1_000_000;
    public const int DefaultQualityMinDecided = 20;
    public const int MaxQualityMinDecided = 100_000;
    public const decimal DefaultQualityMinAgreement = 0.6m;
    public const int MaxApprovedModels = 10;

    private static readonly string[] SettingKeys =
    [
        "ai.enabled",
        "ai.suggestions_paused",
        "ai.approved_models",
        "ai.daily_token_budget",
        "ai.daily_task_budget",
        "ai.daily_content_budget",
        "ai.quality_min_decided",
        "ai.quality_min_agreement"
    ];

    public async Task<AiSettingsData> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await db.SiteSettings
            .AsNoTracking()
            .Where(setting => SettingKeys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

        var dailyTokenBudget = ReadInt(settings, "ai.daily_token_budget", DefaultDailyTokenBudget);
        if (dailyTokenBudget < 1)
        {
            dailyTokenBudget = DefaultDailyTokenBudget;
        }

        var dailyTaskBudget = ReadInt(settings, "ai.daily_task_budget", DefaultDailyTaskBudget);
        if (dailyTaskBudget < 1)
        {
            dailyTaskBudget = DefaultDailyTaskBudget;
        }

        var dailyContentBudget = ReadInt(
            settings,
            "ai.daily_content_budget",
            DefaultDailyContentBudget);
        if (dailyContentBudget < 1)
        {
            dailyContentBudget = DefaultDailyContentBudget;
        }

        var qualityMinDecided = ReadInt(
            settings,
            "ai.quality_min_decided",
            DefaultQualityMinDecided);
        if (qualityMinDecided < 1 || qualityMinDecided > MaxQualityMinDecided)
        {
            qualityMinDecided = DefaultQualityMinDecided;
        }

        var qualityMinAgreement = ReadDecimal(
            settings,
            "ai.quality_min_agreement",
            DefaultQualityMinAgreement);
        if (qualityMinAgreement is < 0.1m or > 1m)
        {
            qualityMinAgreement = DefaultQualityMinAgreement;
        }

        return new AiSettingsData(
            ReadBool(settings, "ai.enabled", false),
            ReadBool(settings, "ai.suggestions_paused", false),
            ReadModels(settings),
            dailyTokenBudget,
            dailyTaskBudget,
            dailyContentBudget,
            qualityMinDecided,
            qualityMinAgreement);
    }

    public Task<AiSettingsUpdateResult> UpdateAsync(
        bool enabled,
        bool suggestionsPaused,
        IReadOnlyCollection<ApprovedModel>? approvedModels,
        int dailyTokenBudget,
        bool confirmEnable,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(
            enabled,
            suggestionsPaused,
            approvedModels,
            dailyTokenBudget,
            DefaultDailyTaskBudget,
            DefaultDailyContentBudget,
            DefaultQualityMinDecided,
            DefaultQualityMinAgreement,
            confirmEnable,
            audit,
            cancellationToken);
    }

    public Task<AiSettingsUpdateResult> UpdateAsync(
        bool enabled,
        bool suggestionsPaused,
        IReadOnlyCollection<ApprovedModel>? approvedModels,
        int dailyTokenBudget,
        int dailyTaskBudget,
        int dailyContentBudget,
        bool confirmEnable,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(
            enabled,
            suggestionsPaused,
            approvedModels,
            dailyTokenBudget,
            dailyTaskBudget,
            dailyContentBudget,
            DefaultQualityMinDecided,
            DefaultQualityMinAgreement,
            confirmEnable,
            audit,
            cancellationToken);
    }

    public async Task<AiSettingsUpdateResult> UpdateAsync(
        bool enabled,
        bool suggestionsPaused,
        IReadOnlyCollection<ApprovedModel>? approvedModels,
        int dailyTokenBudget,
        int dailyTaskBudget,
        int dailyContentBudget,
        int qualityMinDecided,
        decimal qualityMinAgreement,
        bool confirmEnable,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return new AiSettingsUpdateResult(AiSettingsOutcome.Forbidden);
        }

        if (!TryNormalizeModels(approvedModels, enabled, out var normalizedModels))
        {
            return new AiSettingsUpdateResult(AiSettingsOutcome.InvalidInput, "models");
        }

        if (dailyTokenBudget is < 1 or > MaxDailyTokenBudget)
        {
            return new AiSettingsUpdateResult(AiSettingsOutcome.InvalidInput, "budget");
        }

        if (dailyTaskBudget is < 1 or > MaxDailyTaskBudget)
        {
            return new AiSettingsUpdateResult(AiSettingsOutcome.InvalidInput, "task-budget");
        }

        if (dailyContentBudget is < 1 or > MaxDailyContentBudget)
        {
            return new AiSettingsUpdateResult(AiSettingsOutcome.InvalidInput, "content-budget");
        }

        if (qualityMinDecided is < 1 or > MaxQualityMinDecided)
        {
            return new AiSettingsUpdateResult(AiSettingsOutcome.InvalidInput, "quality");
        }

        if (qualityMinAgreement is < 0.1m or > 1m)
        {
            return new AiSettingsUpdateResult(AiSettingsOutcome.InvalidInput, "quality");
        }

        var current = await GetAsync(cancellationToken);
        var modelsChanged = !current.ApprovedModels.SequenceEqual(normalizedModels);
        if ((enabled && !current.Enabled) || modelsChanged)
        {
            if (!confirmEnable)
            {
                return new AiSettingsUpdateResult(AiSettingsOutcome.ConfirmationRequired);
            }
        }

        var effectivePaused = enabled && suggestionsPaused;
        var enabledChanged = current.Enabled != enabled;
        var pausedChanged = current.SuggestionsPaused != effectivePaused;
        var budgetChanged = current.DailyTokenBudget != dailyTokenBudget
            || current.DailyTaskBudget != dailyTaskBudget
            || current.DailyContentBudget != dailyContentBudget;
        var qualityChanged = current.QualityMinDecided != qualityMinDecided
            || current.QualityMinAgreement != qualityMinAgreement;

        if (!enabledChanged && !pausedChanged && !modelsChanged && !budgetChanged && !qualityChanged)
        {
            return new AiSettingsUpdateResult(AiSettingsOutcome.Unchanged);
        }

        var now = timeProvider.GetUtcNow();
        var rows = await db.SiteSettings
            .Where(setting => SettingKeys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, cancellationToken);

        if (enabledChanged)
        {
            SetSetting(
                rows,
                "ai.enabled",
                JsonSerializer.SerializeToDocument(enabled),
                audit.ActorId,
                now);
            db.AuditLogs.Add(NewAudit(
                audit,
                "ai.enabled_changed",
                new { enabled },
                now));
        }

        if (pausedChanged)
        {
            SetSetting(
                rows,
                "ai.suggestions_paused",
                JsonSerializer.SerializeToDocument(effectivePaused),
                audit.ActorId,
                now);
            db.AuditLogs.Add(NewAudit(
                audit,
                "ai.suggestions_paused_changed",
                new { suggestionsPaused = effectivePaused },
                now));
        }

        if (modelsChanged)
        {
            SetSetting(
                rows,
                "ai.approved_models",
                JsonSerializer.SerializeToDocument(normalizedModels.Select(model => new
                {
                    name = model.Name,
                    version = model.Version,
                    quantization = model.Quantization
                })),
                audit.ActorId,
                now);
            db.AuditLogs.Add(NewAudit(
                audit,
                "ai.model_approved",
                new { models = normalizedModels },
                now));
        }

        if (budgetChanged)
        {
            SetSetting(
                rows,
                "ai.daily_token_budget",
                JsonSerializer.SerializeToDocument(dailyTokenBudget),
                audit.ActorId,
                now);
            SetSetting(
                rows,
                "ai.daily_task_budget",
                JsonSerializer.SerializeToDocument(dailyTaskBudget),
                audit.ActorId,
                now);
            SetSetting(
                rows,
                "ai.daily_content_budget",
                JsonSerializer.SerializeToDocument(dailyContentBudget),
                audit.ActorId,
                now);
            db.AuditLogs.Add(NewAudit(
                audit,
                "ai.budget_changed",
                new { dailyTokenBudget, dailyTaskBudget, dailyContentBudget },
                now));
        }

        if (qualityChanged)
        {
            SetSetting(
                rows,
                "ai.quality_min_decided",
                JsonSerializer.SerializeToDocument(qualityMinDecided),
                audit.ActorId,
                now);
            SetSetting(
                rows,
                "ai.quality_min_agreement",
                JsonSerializer.SerializeToDocument(qualityMinAgreement),
                audit.ActorId,
                now);
            db.AuditLogs.Add(NewAudit(
                audit,
                "ai.quality_threshold_changed",
                new { qualityMinDecided, qualityMinAgreement },
                now));
        }

        await db.SaveChangesAsync(cancellationToken);
        return new AiSettingsUpdateResult(AiSettingsOutcome.Success);
    }

    private void SetSetting(
        Dictionary<string, SiteSetting> rows,
        string key,
        JsonDocument value,
        Guid actorId,
        DateTimeOffset now)
    {
        if (rows.TryGetValue(key, out var setting))
        {
            setting.Value = value;
            setting.UpdatedAt = now;
            setting.UpdatedBy = actorId;
            return;
        }

        var created = new SiteSetting
        {
            Key = key,
            Value = value,
            UpdatedAt = now,
            UpdatedBy = actorId
        };

        db.SiteSettings.Add(created);
        rows[key] = created;
    }

    private static bool TryNormalizeModels(
        IReadOnlyCollection<ApprovedModel>? models,
        bool enabled,
        out List<ApprovedModel> normalized)
    {
        normalized = [];
        if (models is null || models.Count == 0)
        {
            return !enabled;
        }

        if (models.Count > MaxApprovedModels)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            var name = (model.Name ?? string.Empty).Trim();
            var version = (model.Version ?? string.Empty).Trim();
            var quantization = (model.Quantization ?? string.Empty).Trim();

            if (!IsValidName(name)
                || !IsValidVersion(version)
                || !IsValidVersion(quantization)
                || ContainsPendingPlaceholder(name)
                || ContainsPendingPlaceholder(version)
                || ContainsPendingPlaceholder(quantization)
                || !names.Add(name))
            {
                return false;
            }

            normalized.Add(new ApprovedModel(name, version, quantization));
        }

        return normalized.Count > 0;
    }

    private static bool IsValidName(string value)
    {
        if (value.Length is < 1 or > 80
            || !(char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0])))
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterLower(character)
            || char.IsAsciiDigit(character)
            || character is '.' or '_' or '-');
    }

    private static bool IsValidVersion(string value)
    {
        if (value.Length is < 1 or > 40 || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '-');
    }

    private static bool ContainsPendingPlaceholder(string value)
    {
        return value.Contains("pending-q-02", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, JsonDocument> settings,
        string key,
        bool fallback)
    {
        return settings.TryGetValue(key, out var document)
            && document.RootElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? document.RootElement.GetBoolean()
            : fallback;
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, JsonDocument> settings,
        string key,
        int fallback)
    {
        return settings.TryGetValue(key, out var document)
            && document.RootElement.ValueKind == JsonValueKind.Number
            && document.RootElement.TryGetInt32(out var value)
            ? value
            : fallback;
    }

    private static decimal ReadDecimal(
        IReadOnlyDictionary<string, JsonDocument> settings,
        string key,
        decimal fallback)
    {
        return settings.TryGetValue(key, out var document)
            && document.RootElement.ValueKind == JsonValueKind.Number
            && document.RootElement.TryGetDecimal(out var value)
            ? value
            : fallback;
    }

    private static IReadOnlyList<ApprovedModel> ReadModels(
        IReadOnlyDictionary<string, JsonDocument> settings)
    {
        if (!settings.TryGetValue("ai.approved_models", out var document)
            || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<ApprovedModel>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("name", out var name)
                || !item.TryGetProperty("version", out var version)
                || !item.TryGetProperty("quantization", out var quantization)
                || name.ValueKind != JsonValueKind.String
                || version.ValueKind != JsonValueKind.String
                || quantization.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            models.Add(new ApprovedModel(
                name.GetString() ?? string.Empty,
                version.GetString() ?? string.Empty,
                quantization.GetString() ?? string.Empty));
        }

        return models;
    }

    private static AuditLog NewAudit(
        AuditContext audit,
        string action,
        object metadata,
        DateTimeOffset createdAt)
    {
        return new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = audit.ActorId,
            Action = action,
            TargetType = "site_setting",
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(metadata),
            CreatedAt = createdAt
        };
    }
}