# WhalePod（鲸群）配置参考

| 项目 | 内容 |
|---|---|
| 文档版本 | v0.1.0 |
| 编制日期 | 2026-10-27 |
| 适用组件 | Web、API、后台 worker |
| 需求基线 | [SRS](SRS.md) v0.1.0 |
| 生产上线 | [production-readiness.md](design/production-readiness.md) |

本文是开发/生产配置的单一参考。所有键均可用环境变量（`:` 替换为 `__`）或秘密存储注入；仓库不保存任何生产密钥、连接串或证书。非 Development 启动会校验关键项，缺失即失败；生产注入与回滚步骤见生产手册。

## 1. 运行时与安全

| 配置 | 默认/要求 | 说明 |
|---|---|---|
| `ConnectionStrings:WhalePod` | 必填 | PostgreSQL 连接串；生产使用托管实例并启用 TLS |
| `PublicSite:BaseUrl` | 非 Development 必填绝对 HTTP(S) | canonical、RSS、Sitemap 的站点根地址 |
| `AllowedHosts` | Development `*`；生产必须显式域名 | 生产含 `*` 或为空会拒绝启动；由反向代理/网关同时校验 Host |
| `DataProtection:KeysDirectory` | 非 Development 必填 | Web/API 必须共享同一持久目录；丢失会使会话/CSRF 失效 |
| `Audit:IpHashSecret` | 非 Development ≥32 字符 | 审计 IP HMAC；不可逆 |
| `Auth:DevLoginEnabled` | `false` | 仅 Development 可开启；生产必须关闭 |
| `Auth:BootstrapAdminHandles` | 空 | 登录时一次性提升管理员的 handle；生产通过秘密存储注入 |
| `Auth:AbsoluteSessionHours` | `24` | 会话绝对有效期（1–168 小时）；与 8 小时滑动过期共同生效，logout 递增 SessionVersion 使该账号全部会话失效 |
| `GitHub:ClientId` / `GitHub:ClientSecret` | 非 Development 必填 | GitHub OAuth App 凭据；禁止提交仓库 |
| `GitHub:CallbackPath` | `/auth/github/callback` | 必须与 GitHub App 登记的回调 HTTPS 地址同域一致 |
| `GitHub:AllowedLogins` / `AllowedOrganizations` | 空 | 可选登录白名单；非空时未命中的账号拒绝登录 |
| `Account:GitHubHmacBlacklistEnabled` | `false` | 注销后保留 GitHub ID HMAC 黑名单；启用前必须完成法务确认 |
| `Account:GitHubHmacSecret` | 未设置 | 启用 HMAC 黑名单时必填 ≥32 字符 |

## 2. 写保护、请求体与计数器

| 配置 | 默认/要求 | 说明 |
|---|---|---|
| `ApiWriteRateLimit:PermitLimit` / `WindowSeconds` | `60` / `60` | API 写端点按用户或来源地址限流 |
| `ApiUploadRateLimit:PermitLimit` / `WindowSeconds` | `20` / `60` | 图片上传独立限流 |
| `ApiRequestLimits:MaxJsonBodyBytes` | `8388608`（8 MiB） | JSON 写请求体上限；分块请求同样受限 |
| `ApiRequestLimits:MaxImageUploadRequestBytes` | `11534336`（11 MiB） | 图片上传 multipart 上限 |
| `WebWriteRateLimit:PermitLimit` / `WindowSeconds` | `60` / `60` | Web SSR POST 表单全局限流；显式策略端点跳过 |
| `WebRequestLimits:MaxFormBodyBytes` | `2097152`（2 MiB） | Web 表单上限；urlencoded 400、multipart 413 |
| `WebRequestLimits:MaxImageUploadBytes` | `11534336`（11 MiB） | `/editor/images` 上传请求上限，需覆盖 10 MiB 图片与 multipart 开销 |
| `InviteRedeemRateLimit:PermitLimit` / `WindowSeconds` | `10` / `60` | 邀请兑换按账号 + 来源地址独立限流 |
| `PublicSearch:RateLimitPerMinute` | `30` | 公开搜索限流 |
| `PublicSearch:TimeoutSeconds` | `3` | 公开搜索查询超时 |
| `ViewCounter:DedupeSecret` | 非 Development ≥32 字符 | 浏览量去重 HMAC |
| `ViewCounter:DedupeWindowSeconds` | `600` | 浏览量去重窗口 |
| `CopyCounter:DedupeSecret` | 非 Development ≥32 字符 | 复制计数去重 HMAC |
| `CopyCounter:BucketSeconds` | `600` | 复制计数去重桶 |
| `DeviceVerification:MaxActiveDevices` | `5` | 每账号活跃设备上限；完成验证在用户行锁内校验 |
| `DeviceVerification:ChallengeTtlSeconds` / `TimestampSkewSeconds` | `300` / `300` | challenge 有效期与签名时间偏移 |
| `DeviceVerification:MaxFailedAttempts` / `FailureWindowSeconds` / `LockoutSeconds` | `5` / `600` / `300` | 验证失败锁定策略 |
| `DeviceVerification:ChallengeRateLimitPerMinute` / `CompleteRateLimitPerMinute` / `WriteRateLimitPerMinute` | `10` / `10` / `30` | 设备端点限流 |
| `DeviceVerification:ChallengeRetentionHours` / `ChallengeCleanupIntervalMinutes` | `24` / `10` | challenge 清理 |
| `AgentRateLimit:ReadPerMinute` / `WritePerMinute` / `HeartbeatPerMinute` | `120` / `60` / `12` | Agent API 读/写/心跳限流 |

## 3. 媒体存储

| 配置 | 默认/要求 | 说明 |
|---|---|---|
| `MediaStorage:Provider` | `S3`（Development 为 `Local`） | 非 Development 只允许 S3 兼容存储 |
| `MediaStorage:PublicBaseUrl` | `/media`（Development） | 图片公开 URL 前缀；S3 可为 CDN 绝对地址 |
| `MediaStorage:LocalRootPath` | `%LOCALAPPDATA%\whalepod\media` | 仅 Local；仓库不保存上传文件 |
| `MediaStorage:S3:ServiceUrl` / `BucketName` | 生产必填 | COS/MinIO 等 S3 兼容端点与桶 |
| `MediaStorage:S3:Region` | `ap-guangzhou` | 签名区域 |
| `MediaStorage:S3:AccessKeyId` / `SecretAccessKey` | 未设置 | 成对配置或使用实例角色；禁止提交仓库 |
| `MediaStorage:S3:ForcePathStyle` | `true` | COS/MinIO 通常需要路径风格 |
| `MediaStorage:CleanupIntervalMinutes` / `CleanupBatchSize` | `60` / `100` | 临时/已删除上传清理 |

## 4. 邮件

| 配置 | 默认/要求 | 说明 |
|---|---|---|
| `Email:Provider` | `Smtp`（Development 为 `Logging`） | 非 Development 只允许 SMTP |
| `Email:PublicBaseUrl` | Development `http://localhost:5224` | 邮件深链/偏好/退订链接的站点根地址；生产必须 HTTPS |
| `Email:SuppressionHashSecret` / `Email:UnsubscribeSecret` | 生产必填 ≥32 字符 | 退信抑制 HMAC 与退订签名 |
| `Email:Smtp:Host` / `Port` / `Security` / `FromAddress` | 空 / `587` / `StartTls` / 空 | SMTP 服务器；生产必填 Host 与 FromAddress，且 `Security` 不得为 `None`/`Auto`/`StartTlsWhenAvailable` |
| `Email:Smtp:UserName` / `Password` | 未设置 | 可选认证；从秘密存储注入 |
| `Email:Smtp:TimeoutSeconds` | `30` | SMTP 超时 |
| `Email:PollIntervalSeconds` / `BatchSize` / `LeaseSeconds` | `30` / `20` / `300` | 发件箱轮询、单批与租约 |
| `Email:MaxAttempts` / `BackoffBaseSeconds` / `BackoffMaxSeconds` | `5` / `60` / `3600` | 失败重试上限与指数退避 |
| `Email:UnsubscribeTokenLifetimeDays` | `30` | 签名退订链接有效期 |

## 5. 保留期与健康阈值

| 配置 | 默认 | 说明 |
|---|---|---|
| `Retention:NotificationRetentionDays` | `180` | 通知保留期 |
| `Retention:EmailOutboxRetentionDays` | `30` | 已结束邮件保留期；pending/sending 不删除 |
| `Retention:CleanupIntervalMinutes` / `BatchSize` | `10` / `2000` | API 保留期清理周期/单批 |
| `Health:EmailBacklogWarningThreshold` | `50` | 邮件积压告警 |
| `Health:MediaCleanupBacklogWarningThreshold` | `100` | 媒体清理积压告警 |
| `Health:DeviceChallengeBacklogWarningThreshold` | `100` | challenge 积压告警 |
| `Health:CopyDedupeBacklogWarningThreshold` | `1000` | 去重键积压告警 |
| `Health:ModerationTaskBacklogWarningThreshold` | `100` | Agent 队列积压告警 |
| `Operations:Backup:StatusFilePath` | 空 | 备份脚本状态 JSON；空时健康页提示未配置 |

## 6. 反向代理

| 配置 | 默认/要求 | 说明 |
|---|---|---|
| `ReverseProxy:ForwardedHeadersEnabled` | `false` | 启用时必须配置 KnownProxies 或 KnownNetworks |
| `ReverseProxy:KnownProxies` | `[]` | 可信反向代理 IP 列表 |
| `ReverseProxy:KnownNetworks` | `[]` | 可信代理 CIDR 列表 |
| `ReverseProxy:ForwardLimit` | `1` | 信任的代理跳数 |

## 7. 数据库中的站点配置

以下配置存于 `SiteSetting`，由管理员页面维护，不是环境变量：

- `ai.enabled`、`ai.suggestions_paused`、`ai.approved_models`
- `ai.daily_token_budget`、`ai.daily_task_budget`、`ai.daily_content_budget`
- `ai.quality_min_decided`、`ai.quality_min_agreement`
- `moderation.sensitive_words_policy`
- `site.read_only`、`site.login_disabled`、`site.read_only_allow_member_write`

生产秘密仍通过环境变量/秘密存储注入，不得写入 `SiteSetting`。
