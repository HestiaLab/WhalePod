using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Storage;

namespace WhalePod.Infrastructure.Services;

public sealed record SystemHealthData(
    bool DatabaseReachable,
    int PendingMigrations,
    int AppliedMigrations,
    int EmailPending,
    int EmailSending,
    int EmailFailed,
    int EmailSuppressed,
    int EmailSent,
    DateTimeOffset? OldestEmailBacklogAt,
    bool AiEnabled,
    bool SuggestionsPaused,
    int ApprovedModelCount,
    int ActiveAgents,
    int OfflineAgents,
    DateTimeOffset? LastAgentHeartbeatAt,
    int ModerationChecks24h,
    int ModerationErrors24h,
    bool BackupConfigured,
    DateTimeOffset? LastBackupAt,
    bool StorageConfigured,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CheckedAt,
    int MediaCleanupBacklog,
    int ExpiredChallenges,
    int ExpiredCopyDedupes,
    int NotificationRetentionBacklog,
    int EmailRetentionBacklog,
    int ModerationTasksQueued,
    int ModerationTasksClaimed,
    int ModerationTaskExpiredLeases,
    string StorageProvider,
    long AiTokensUsedToday,
    int AiDailyTokenBudget,
    bool AiBudgetExceeded,
    bool AiFailureCircuitOpen,
    int AiRecentFailures,
    int AiDailyTaskBudget,
    int AiDailyContentBudget,
    int AiTodaySuggestions,
    int AiTodayTasks,
    bool AiTaskBudgetExceeded,
    bool AiContentBudgetExceeded);

public sealed class SystemHealthService(
    WhalePodDbContext db,
    AiSettingsService aiSettings,
    AiBudgetService aiBudgetService,
    IOptions<RetentionOptions> retentionOptions,
    IOptions<HealthOptions> healthOptions,
    IOptions<MediaStorageOptions> mediaStorageOptions,
    BackupStatusProvider backupStatusProvider,
    TimeProvider timeProvider,
    ILogger<SystemHealthService> logger)
{
    public async Task<SystemHealthData> GetAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = timeProvider.GetUtcNow();
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return Unreachable(checkedAt);
            }

            var pendingMigrations = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
            var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Count();

            var emailCounts = await db.EmailOutbox
                .AsNoTracking()
                .GroupBy(email => email.Status)
                .Select(group => new { Status = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.Status, item => item.Count, cancellationToken);

            var oldestBacklog = await db.EmailOutbox
                .AsNoTracking()
                .Where(email => email.Status == EmailStatuses.Pending || email.Status == EmailStatuses.Sending)
                .MinAsync(email => (DateTimeOffset?)email.CreatedAt, cancellationToken);

            AiSettingsData ai;
            AiBudgetStatusView aiBudget;
            var aiDegraded = false;
            try
            {
                ai = await aiSettings.GetAsync(cancellationToken);
                aiBudget = await aiBudgetService.GetAsync(ai, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "AI health metrics could not be read; continuing with the remaining checks.");
                var dayStart = new DateTimeOffset(checkedAt.UtcDateTime.Date, TimeSpan.Zero);
                ai = new AiSettingsData(
                    false,
                    false,
                    [],
                    AiSettingsService.DefaultDailyTokenBudget,
                    AiSettingsService.DefaultDailyTaskBudget,
                    AiSettingsService.DefaultDailyContentBudget,
                    AiSettingsService.DefaultQualityMinDecided,
                    AiSettingsService.DefaultQualityMinAgreement);
                aiBudget = new AiBudgetStatusView(
                    false,
                    false,
                    AiSettingsService.DefaultDailyTokenBudget,
                    AiSettingsService.DefaultDailyTaskBudget,
                    AiSettingsService.DefaultDailyContentBudget,
                    0,
                    AiSettingsService.DefaultDailyTokenBudget,
                    0,
                    0,
                    false,
                    false,
                    false,
                    0,
                    false,
                    false,
                    dayStart,
                    dayStart.AddDays(1),
                    checkedAt);
                aiDegraded = true;
            }

            var heartbeatThreshold = checkedAt.AddMinutes(-5);
            var activeAgents = await db.AgentInstances
                .AsNoTracking()
                .CountAsync(agent => agent.Status == AgentInstanceStatuses.Active, cancellationToken);
            var offlineAgents = await db.AgentInstances
                .AsNoTracking()
                .CountAsync(
                    agent => agent.Status == AgentInstanceStatuses.Active
                        && (agent.LastHeartbeatAt == null || agent.LastHeartbeatAt < heartbeatThreshold),
                    cancellationToken);
            var lastHeartbeat = await db.AgentInstances
                .AsNoTracking()
                .MaxAsync(agent => (DateTimeOffset?)agent.LastHeartbeatAt, cancellationToken);

            var moderationFrom = checkedAt.AddHours(-24);
            var moderationCounts = await db.ModerationChecks
                .AsNoTracking()
                .Where(check => check.CreatedAt >= moderationFrom)
                .GroupBy(check => check.Result)
                .Select(group => new { Result = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.Result, item => item.Count, cancellationToken);

            var retention = retentionOptions.Value;
            var thresholds = healthOptions.Value;
            var notificationCutoff = checkedAt.AddDays(-Math.Max(1, retention.NotificationRetentionDays));
            var emailCutoff = checkedAt.AddDays(-Math.Max(1, retention.EmailOutboxRetentionDays));

            var mediaCleanupBacklog = await db.MediaUploads
                .AsNoTracking()
                .CountAsync(
                    upload => upload.Status == MediaUploadStatuses.Deleted
                        || (upload.Status == MediaUploadStatuses.Temp && upload.ExpiresAt <= checkedAt),
                    cancellationToken);
            var expiredChallenges = await db.DeviceChallenges
                .AsNoTracking()
                .CountAsync(challenge => challenge.ExpiresAt <= checkedAt, cancellationToken);
            var expiredCopyDedupes = await db.CopyDedupes
                .AsNoTracking()
                .CountAsync(dedupe => dedupe.ExpiresAt <= checkedAt, cancellationToken);
            var notificationRetentionBacklog = await db.Notifications
                .AsNoTracking()
                .CountAsync(notification => notification.CreatedAt < notificationCutoff, cancellationToken);
            var emailRetentionBacklog = await db.EmailOutbox
                .AsNoTracking()
                .CountAsync(
                    email => email.CreatedAt < emailCutoff
                        && (email.Status == EmailStatuses.Sent
                            || email.Status == EmailStatuses.Failed
                            || email.Status == EmailStatuses.Suppressed),
                    cancellationToken);
            var queuedTasks = await db.ModerationTasks
                .AsNoTracking()
                .CountAsync(
                    task => task.Status == ModerationTaskStatuses.Queued
                        || task.Status == ModerationTaskStatuses.Escalated,
                    cancellationToken);
            var claimedTasks = await db.ModerationTasks
                .AsNoTracking()
                .CountAsync(task => task.Status == ModerationTaskStatuses.Claimed, cancellationToken);
            var expiredTaskLeases = await db.ModerationTasks
                .AsNoTracking()
                .CountAsync(
                    task => task.Status == ModerationTaskStatuses.Claimed
                        && task.LeaseExpiresAt != null
                        && task.LeaseExpiresAt < checkedAt,
                    cancellationToken);

            var emailPending = emailCounts.GetValueOrDefault(EmailStatuses.Pending);
            var emailSending = emailCounts.GetValueOrDefault(EmailStatuses.Sending);
            var emailFailed = emailCounts.GetValueOrDefault(EmailStatuses.Failed);
            var moderationChecks = moderationCounts.Values.Sum();
            var moderationErrors = moderationCounts.GetValueOrDefault(ModerationCheckResults.Error);
            var storageProvider = mediaStorageOptions.Value.Provider ?? string.Empty;
            var storageConfigured = !string.IsNullOrWhiteSpace(storageProvider);
            BackupStatus backup;
            try
            {
                backup = backupStatusProvider.Read();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Backup status check failed with {ErrorType}; other health checks continue.",
                    exception.GetType().Name);
                backup = new BackupStatus(true, null, null, null);
            }

            var warnings = new List<string>();
            if (aiDegraded)
            {
                warnings.Add("AI 状态读取失败，其余健康检查继续。");
            }

            if (pendingMigrations > 0)
            {
                warnings.Add("存在未应用的数据库迁移。");
            }

            if (emailFailed > 0)
            {
                warnings.Add($"邮件发送失败 {emailFailed} 封。");
            }

            if (emailPending + emailSending > Math.Max(0, thresholds.EmailBacklogWarningThreshold))
            {
                warnings.Add("邮件队列有积压，请检查发件箱 worker 与邮件服务。");
            }

            if (ai.Enabled && ai.ApprovedModels.Count == 0)
            {
                warnings.Add("AI 已启用但未配置批准模型。");
            }

            if (ai.Enabled && activeAgents == 0)
            {
                warnings.Add("AI 已启用但没有在线 Agent。");
            }

            if (ai.Enabled && aiBudget.BudgetExceeded)
            {
                warnings.Add(
                    $"AI 每日 token 预算已用尽（{aiBudget.UsedTokensToday}/{aiBudget.DailyTokenBudget}），claim/suggestion 已阻断，请人工审核。");
            }

            if (ai.Enabled && aiBudget.TaskBudgetExceeded)
            {
                warnings.Add(
                    $"AI 每日任务量预算已用尽（{aiBudget.TodaySuggestionCount}/{aiBudget.DailyTaskBudget}），claim/suggestion 已阻断。");
            }

            if (ai.Enabled && aiBudget.ContentBudgetExceeded)
            {
                warnings.Add(
                    $"AI 每日内容量预算已用尽（{aiBudget.TodayCreatedTaskCount}/{aiBudget.DailyContentBudget}），已停止生成新任务。");
            }

            if (ai.Enabled && aiBudget.FailureCircuitOpen)
            {
                warnings.Add(
                    $"AI 审核自上次成功建议以来近 {AiBudgetService.FailureWindowMinutes} 分钟失败 {aiBudget.RecentFailures} 次（过期租约/非法建议），已熔断，请人工审核。");
            }

            if (offlineAgents > 0)
            {
                warnings.Add($"有 {offlineAgents} 个 Agent 心跳超时。");
            }

            if (moderationErrors > 0)
            {
                warnings.Add("近 24 小时存在机审错误，需人工兜底。");
            }

            if (mediaCleanupBacklog > Math.Max(0, thresholds.MediaCleanupBacklogWarningThreshold))
            {
                warnings.Add($"媒体清理积压 {mediaCleanupBacklog} 条，请检查清理 worker 与对象存储。");
            }

            if (expiredChallenges > Math.Max(0, thresholds.DeviceChallengeBacklogWarningThreshold))
            {
                warnings.Add($"过期设备 challenge 积压 {expiredChallenges} 条，请检查清理 worker。");
            }

            if (expiredCopyDedupes > Math.Max(0, thresholds.CopyDedupeBacklogWarningThreshold))
            {
                warnings.Add($"复制去重键积压 {expiredCopyDedupes} 条，请检查保留期清理。");
            }

            if (notificationRetentionBacklog > 0)
            {
                warnings.Add($"有 {notificationRetentionBacklog} 条通知超过保留期未清理。");
            }

            if (emailRetentionBacklog > 0)
            {
                warnings.Add($"有 {emailRetentionBacklog} 封已结束邮件超过保留期未清理。");
            }

            if (expiredTaskLeases > 0)
            {
                warnings.Add($"有 {expiredTaskLeases} 个 Agent 任务租约已过期，等待回收或人工兜底。");
            }

            if (queuedTasks + claimedTasks > Math.Max(0, thresholds.ModerationTaskBacklogWarningThreshold))
            {
                warnings.Add("AI 审核任务队列积压，请检查 Agent 在线状态与人工队列。");
            }

            if (!backup.Configured)
            {
                warnings.Add("未配置备份状态文件，无法确认最近一次备份。");
            }
            else if (backup.LastSuccessAt is null)
            {
                warnings.Add("尚未发现最近一次备份成功记录。");
            }
            else if (backup.LastSuccessAt > checkedAt.AddHours(1))
            {
                warnings.Add("备份状态时间晚于当前时间，请检查服务器时钟。");
            }
            else if (backup.LastSuccessAt < checkedAt.AddHours(-24))
            {
                warnings.Add("最近一次备份已超过 24 小时，请检查备份任务。");
            }

            if (!storageConfigured)
            {
                warnings.Add("对象存储未配置。");
            }

            return new SystemHealthData(
                DatabaseReachable: true,
                PendingMigrations: pendingMigrations,
                AppliedMigrations: appliedMigrations,
                EmailPending: emailPending,
                EmailSending: emailSending,
                EmailFailed: emailFailed,
                EmailSuppressed: emailCounts.GetValueOrDefault(EmailStatuses.Suppressed),
                EmailSent: emailCounts.GetValueOrDefault(EmailStatuses.Sent),
                OldestEmailBacklogAt: oldestBacklog,
                AiEnabled: ai.Enabled,
                SuggestionsPaused: ai.SuggestionsPaused,
                ApprovedModelCount: ai.ApprovedModels.Count,
                ActiveAgents: activeAgents,
                OfflineAgents: offlineAgents,
                LastAgentHeartbeatAt: lastHeartbeat,
                ModerationChecks24h: moderationChecks,
                ModerationErrors24h: moderationErrors,
                BackupConfigured: backup.Configured,
                LastBackupAt: backup.LastSuccessAt,
                StorageConfigured: storageConfigured,
                Warnings: warnings,
                CheckedAt: checkedAt,
                MediaCleanupBacklog: mediaCleanupBacklog,
                ExpiredChallenges: expiredChallenges,
                ExpiredCopyDedupes: expiredCopyDedupes,
                NotificationRetentionBacklog: notificationRetentionBacklog,
                EmailRetentionBacklog: emailRetentionBacklog,
                ModerationTasksQueued: queuedTasks,
                ModerationTasksClaimed: claimedTasks,
                ModerationTaskExpiredLeases: expiredTaskLeases,
                StorageProvider: storageProvider,
                AiTokensUsedToday: aiBudget.UsedTokensToday,
                AiDailyTokenBudget: aiBudget.DailyTokenBudget,
                AiBudgetExceeded: aiBudget.BudgetExceeded,
                AiFailureCircuitOpen: aiBudget.FailureCircuitOpen,
                AiRecentFailures: aiBudget.RecentFailures,
                AiDailyTaskBudget: aiBudget.DailyTaskBudget,
                AiDailyContentBudget: aiBudget.DailyContentBudget,
                AiTodaySuggestions: aiBudget.TodaySuggestionCount,
                AiTodayTasks: aiBudget.TodayCreatedTaskCount,
                AiTaskBudgetExceeded: aiBudget.TaskBudgetExceeded,
                AiContentBudgetExceeded: aiBudget.ContentBudgetExceeded);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "System health check failed.");
            return Unreachable(checkedAt);
        }
    }

    private static SystemHealthData Unreachable(DateTimeOffset checkedAt)
    {
        return new SystemHealthData(
            DatabaseReachable: false,
            PendingMigrations: 0,
            AppliedMigrations: 0,
            EmailPending: 0,
            EmailSending: 0,
            EmailFailed: 0,
            EmailSuppressed: 0,
            EmailSent: 0,
            OldestEmailBacklogAt: null,
            AiEnabled: false,
            SuggestionsPaused: false,
            ApprovedModelCount: 0,
            ActiveAgents: 0,
            OfflineAgents: 0,
            LastAgentHeartbeatAt: null,
            ModerationChecks24h: 0,
            ModerationErrors24h: 0,
            BackupConfigured: false,
            LastBackupAt: null,
            StorageConfigured: false,
            Warnings: ["数据库不可达，无法读取健康指标。"],
            CheckedAt: checkedAt,
            MediaCleanupBacklog: 0,
            ExpiredChallenges: 0,
            ExpiredCopyDedupes: 0,
            NotificationRetentionBacklog: 0,
            EmailRetentionBacklog: 0,
            ModerationTasksQueued: 0,
            ModerationTasksClaimed: 0,
            ModerationTaskExpiredLeases: 0,
            StorageProvider: string.Empty,
            AiTokensUsedToday: 0,
            AiDailyTokenBudget: 0,
            AiBudgetExceeded: false,
            AiFailureCircuitOpen: false,
            AiRecentFailures: 0,
            AiDailyTaskBudget: 0,
            AiDailyContentBudget: 0,
            AiTodaySuggestions: 0,
            AiTodayTasks: 0,
            AiTaskBudgetExceeded: false,
            AiContentBudgetExceeded: false);
    }
}