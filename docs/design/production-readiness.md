# WhalePod（鲸群）生产上线与回滚手册

| 项目 | 内容 |
|---|---|
| 文档版本 | v0.1.0 |
| 编制日期 | 2026-10-27 |
| 适用环境 | 预发/生产（腾讯云 TKE/CVM + TencentDB PostgreSQL + COS + CDN/WAF） |
| 需求基线 | [SRS](../SRS.md) v0.1.0；REQ-NF-014/015/016/019/020/022 |
| 当前状态 | 本地交付与演练完成；**未连接、未修改任何预发/生产资源**。生产启用前置条件见第 8 节 |
| 相关文档 | [configuration.md](../configuration.md)、[acceptance.md](acceptance.md)、[performance.md](performance.md)、[architecture.md](architecture.md) |

本文是上线操作手册，不是生产验收证明。生产迁移、真实 OAuth/SMTP/COS、WAF/备案与容量压测必须在目标环境单独完成。

## 1. 生产启动配置

完整配置键、默认值与必填项见 [configuration.md](../configuration.md)；本手册只列生产上线必须确认的类别。

- **数据库**：`ConnectionStrings:WhalePod`，托管 PostgreSQL + TLS。
- **站点**：`PublicSite:BaseUrl` 为已备案 HTTPS 域名；`AllowedHosts` 必须显式列出域名，生产含 `*` 或为空会拒绝启动。
- **安全头**：Web/API 统一发送 `Referrer-Policy`、`X-Content-Type-Options`、`X-Frame-Options`、`Permissions-Policy`；Web 额外发送 `Content-Security-Policy-Report-Only`（先观察，再收紧为强制策略）。
- **就绪**：`/healthz/ready` 校验数据库可达、无待应用迁移与关键表探测；空库/旧 schema 返回 503，不得切流。
- **会话**：`DataProtection:KeysDirectory` 持久化并由 Web/API 共享；`Audit:IpHashSecret`、`ViewCounter:DedupeSecret`、`CopyCounter:DedupeSecret` 均 ≥32 字符。
- **认证**：`Auth:DevLoginEnabled=false`；GitHub OAuth 凭据/回调/白名单已配置；`Auth:BootstrapAdminHandles` 通过秘密存储注入。
- **邮件**：`Email:Provider=Smtp`，SMTP Host/FromAddress、抑制与退订密钥齐全；`Email:PublicBaseUrl` 必须为 HTTPS；`Email:Smtp:Security` 不得为 `None`/`Auto`/`StartTlsWhenAvailable`。
- **媒体**：`MediaStorage:Provider=S3`，端点/桶/区域/CDN 前缀齐全；AccessKey 成对注入或使用实例角色。
- **代理**：仅在配置 `KnownProxies`/`KnownNetworks` 后启用 `ReverseProxy:ForwardedHeadersEnabled`。
- **站点模式**：生产默认关闭 HMAC 黑名单；法务确认后再启用 `Account:GitHubHmacBlacklistEnabled`。

非 Development 启动会逐项校验关键项，缺失即失败。校验已在本机使用临时端口与独立 DP 目录验证；未连接生产。

## 2. 秘密注入

建议使用腾讯云 Secret Manager + TKE Secret/环境变量，或 CVM 的受保护环境文件；不要写入镜像、命令行、Git、CI 日志或提交版本。

```text
ConnectionStrings__WhalePod=<托管 PostgreSQL 连接串，sslmode=require>
DataProtection__KeysDirectory=/var/lib/whalepod/dpkeys
Audit__IpHashSecret=<≥32 字符随机值>
ViewCounter__DedupeSecret=<≥32 字符随机值>
CopyCounter__DedupeSecret=<≥32 字符随机值>
Email__Provider=Smtp
Email__PublicBaseUrl=https://<站点域名>
Email__SuppressionHashSecret=<≥32 字符随机值>
Email__UnsubscribeSecret=<≥32 字符随机值>
Email__Smtp__Host=<SMTP 主机>
Email__Smtp__Port=587
Email__Smtp__Security=StartTls
Email__Smtp__FromAddress=<发件地址>
Email__Smtp__UserName=<账号>
Email__Smtp__Password=<密码/授权码>
MediaStorage__Provider=S3
MediaStorage__S3__ServiceUrl=https://cos.<region>.myqcloud.com
MediaStorage__S3__BucketName=<桶名>
MediaStorage__S3__Region=ap-guangzhou
MediaStorage__S3__ForcePathStyle=true
MediaStorage__PublicBaseUrl=https://<CDN 域名>/media
PublicSite__BaseUrl=https://<站点域名>
ReverseProxy__ForwardedHeadersEnabled=true
ReverseProxy__KnownNetworks__0=<CLB/WAF 私网网段>
ReverseProxy__KnownProxies__0=<代理固定 IP，可选>
Operations__Backup__StatusFilePath=/var/lib/whalepod/backup-status.json
```

- `DataProtection:KeysDirectory` 必须持久化并备份；Web/API 共用，丢失会导致所有会话/防伪令牌失效。
- `Audit:IpHashSecret`、去重密钥轮换会使旧 HMAC/去重键不可关联；轮换前评估审计关联与去重窗口。
- 生产不得打开 `Auth:DevLoginEnabled`；GitHub OAuth 代码与 fake 测试已完成，但真实 App 凭据、回调域名与近期重认证仍需外部验收。

## 3. 数据库迁移与发布顺序

前置：完成备份、迁移评审与预发演练；迁移脚本只向前兼容。

```bash
export WHALEPOD_DB_CONNECTION="Host=<host>;Port=5432;Database=<db>;Username=<user>;Password=<secret>;SSL Mode=Require"
dotnet tool restore
dotnet tool run dotnet-ef migrations has-pending-model-changes \
  --project src/WhalePod.Infrastructure --startup-project src/WhalePod.Infrastructure
dotnet tool run dotnet-ef database update \
  --project src/WhalePod.Infrastructure --startup-project src/WhalePod.Infrastructure
```

发布顺序（滚动/蓝绿）：

1. 备份数据库与对象存储清单；记录当前镜像版本、迁移版本、DP 目录版本。
2. 应用向后兼容迁移（先加列/表/索引，不立即删除；当前迁移集最新为 `AddLeaseFencingAndCleanupBackoff`，含会话版本、租约 fencing、媒体退避与通知链接字段）。
3. 部署新 Web/API 镜像；健康检查通过后切流。
4. 验证 `/healthz`、`/healthz/ready`、`/admin/system`、登录/发帖/搜索/RSS；观察 5–10 分钟错误率与队列。
5. 保留旧镜像与数据库快照，进入观察期。

当前迁移集已在本机可弃库验证 Up/Down 与空库/旧 schema 的 ready 行为，`has-pending-model-changes` 无输出；生产/预发尚未应用任何迁移。

**迁移回退策略：不支持数据库降级。** 已有 `appeal_result` 通知/头像等新数据时，部分迁移的 `Down` 会失败或丢失数据；回滚统一采用“上一版本镜像 + 备份恢复到新库 + 恢复对象存储/DP 目录”，详见 [deploy/README.md](../../deploy/README.md)。数据损失窗口 = 最近一次成功备份至故障发生。

## 4. 备份与恢复

脚本：

- [scripts/backup-postgres.ps1](../../scripts/backup-postgres.ps1) / [scripts/backup-postgres.sh](../../scripts/backup-postgres.sh)
- [scripts/restore-postgres.ps1](../../scripts/restore-postgres.ps1) / [scripts/restore-postgres.sh](../../scripts/restore-postgres.sh)

特性：自定义格式压缩、`--no-owner --no-privileges`、SHA-256 清单（恢复时必须存在）、可选状态文件（健康页读取）、默认 30 天清理、恢复强制 `--yes` + `--confirm-database` + 目标名二次确认；目标库已存在且非空时默认拒绝，需显式 `--allow-existing`，此时使用 `--clean --single-transaction`；新库/空库同样启用 `--single-transaction`，恢复失败不会留下半恢复库；连接串支持带引号的密码/分号并传递 `SSL Mode`、根证书、客户端证书/密钥；数据库名仅允许 `whalepod` 或 `whalepod_<小写后缀>`；不打印连接串/密码；不包含仓库密钥。`--dry-run` / `-DryRun` 可校验参数、校验和与连接设置而不执行恢复。

```bash
export WHALEPOD_DB_CONNECTION="Host=127.0.0.1;Port=55432;Database=whalepod;Username=postgres;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/ca.pem"
export PG_BIN="/c/Program Files/PostgreSQL/18/bin"          # 或 PATH 中已有 pg_dump/pg_restore
export WHALEPOD_BACKUP_DIR="$PWD/artifacts/backups"
export WHALEPOD_BACKUP_STATUS_FILE="$PWD/artifacts/backups/status.json"
./scripts/backup-postgres.sh

# 恢复到新/空库；必须显式确认；不要在生产直接执行
./scripts/restore-postgres.sh \
  --backup artifacts/backups/whalepod-whalepod-<timestamp>.dump \
  --database whalepod_restore_20261028 \
  --confirm-database whalepod_restore_20261028 --yes

# 显式覆盖非空库（单事务 + clean，失败自动回滚）
./scripts/restore-postgres.sh \
  --backup artifacts/backups/whalepod-whalepod-<timestamp>.dump \
  --database whalepod_restore_20261028 \
  --confirm-database whalepod_restore_20261028 --allow-existing --yes
```

Windows PowerShell 用法：

```powershell
$env:WHALEPOD_DB_CONNECTION = "Host=127.0.0.1;Port=55432;Database=whalepod;Username=postgres;SSL Mode=VerifyFull;Root Certificate=C:\ssl\ca.pem"
$env:PG_BIN = "C:\Program Files\PostgreSQL\18\bin"
.\scripts\backup-postgres.ps1
.\scripts\restore-postgres.ps1 -BackupFile .\artifacts\backups\<file>.dump `
  -Database whalepod_restore_20261028 -ConfirmDatabase whalepod_restore_20261028 -Yes
```

本机恢复演练已完成：备份开发库后恢复到独立库 `whalepod_restore_test`，迁移/表计数、`citext`/`pg_trgm` 扩展与 `trg_audit_log_append_only` 触发器核对一致；审计更新被触发器拒绝。生产备份、异地/异账号副本、加密与每月恢复演练仍需运维执行。

RPO/RTO：SRS 要求每日全量备份、RPO ≤24h、RTO ≤4h、保留 ≥30 天、加密；脚本只提供数据库逻辑备份，上传文件/对象存储版本与跨地域复制需在 COS 侧配置。

## 5. 可观测性、队列与告警

### 健康检查

| 端点 | 用途 |
|---|---|
| `GET /healthz`（Web/API） | 存活检查，不访问数据库 |
| `GET /healthz/ready`（Web/API） | 就绪检查，检查数据库可达；不可达返回 503 |
| `GET /admin/system` | 管理员只读：迁移、邮件队列、AI/Agent、机审 24h、保留期/清理积压、备份状态、存储状态；不展示连接串/密钥 |

### 结构化日志

- Web/API 每个请求输出 `http_request`：`method`、`path`（不含 query）、`status`、`elapsedMs`、`requestId`、`userRef`；`userRef` 是 `Audit:IpHashSecret` HMAC 前 6 字节十六进制，不记录原始用户 ID、IP、邮箱或令牌。
- 响应头 `X-Request-Id` 使用 ASP.NET Core TraceIdentifier；异常日志与审计 `RequestId` 可用于关联。
- 关键 worker 输出清理/发送计数与失败类型；邮件正文、收件地址、令牌、原始 IP 不进入日志。
- 建议日志采集到腾讯云 CLS，保留 ≥6 个月，并设置：
  - `status>=500` 突增；
  - `path=/internal/counters/copy status=429` 异常；
  - 邮件/保留期/媒体/challenge/Agent 队列积压阈值（`Health` 配置）；
  - `Scheduled retention cleanup failed`、`Device challenge cleanup failed`、`Scheduled media upload cleanup failed`、`Database readiness check failed`。

### 保留期与告警配置

保留期、健康阈值与备份状态文件配置见 [configuration.md](../configuration.md) 第 5 节。API 后台 worker 负责邮件发件箱、媒体临时/已删除清理、设备 challenge 清理与保留期清理；所有 worker 只在 API 进程运行，Web 不运行，外部 SMTP/S3 调用不在请求事务内。

## 6. CORS、同源与反向代理

- **仓库不启用 CORS**；Web/API 必须同站部署（同 host，可不同端口）或由同域反向代理把 `/api/v1` 转发到 API。不得配置 `Access-Control-Allow-Origin: *`。
- 浏览器写操作依赖同源检查 + `whalepod_csrf` + `X-CSRF-Token`；拆分 API 子域会使 host-only Cookie 与 CSRF 失效，必须另行评审 Cookie Domain、CORS、SameSite 与 CSRF。
- 反向代理必须覆盖/清理外部传入的 `X-Forwarded-For`/`X-Forwarded-Proto`，保留 Host；应用只信任 `KnownProxies`/`KnownNetworks`，默认 ForwardLimit=1。审计只保存有效客户端地址 HMAC。
- Web/API 共享 Data Protection 目录；滚动发布时所有实例必须挂载同一密钥目录。

## 7. 回滚/回退清单

1. 触发条件：5xx 持续 >5 分钟、登录/写操作大面积失败、迁移导致不可用、队列持续积压或数据异常。
2. 应用回滚：切回上一镜像/版本；保留新版本 Pod 现场与日志。
3. 数据库：**不支持数据库降级**。若新版本已执行不可逆迁移，按备份恢复到新库并切换连接（会丢失恢复点后写入），同时恢复对象存储/DP 目录一致性；容器与命令见 [deploy/README.md](../../deploy/README.md)。
4. 对象存储：启用 COS 版本/生命周期；回滚前记录对象清单，禁止直接删除生产桶。
5. 会话：DP 密钥目录不变则会话保留；若轮换/恢复旧目录，用户需重新登录。
6. 记录：回滚时间、版本、迁移、数据损失窗口、原因与后续动作；目标 10 分钟内恢复（REQ-NF-019）。
7. 不得用生产库做压测/重置；不得删除数据目录或未备份对象。

## 8. 生产启用前置条件

| 项 | 状态 |
|---|---|
| 配置校验、备份脚本、健康/日志、保留期、CI PostgreSQL 18 | 本地完成 |
| 真实 GitHub OAuth、近期重认证 | 待 GitHub App 凭据、回调域名与网络验收 |
| SMTP/COS、退信/投诉、CDN/WAF | 待服务商与云资源 |
| Q-02 模型、200 样本评估、真实 MCP 客户端 + 模型 E2E | 待模型/标注/硬件 |
| 镜像/Compose/迁移/备份/回滚 runbook | 仓库已提供（`deploy/`）；镜像构建在 CI 校验，未推送到镜像仓库 |
| 腾讯云预发迁移、秘密注入、备份恢复演练、UAT-15/16 | 待授权环境（外部运维）；仓库不伪造云上证据 |
| C-09 法务、ICP/公安备案、日志保留与密钥轮换 | 待法务/运维 |
| 真实屏幕阅读器、物理设备、macOS/Linux CLI 实机与签名 | 待人工/签名预算 |
| 完整 10 分钟容量压测与监控阈值校准 | 待预发环境；本地缩短结果见 [performance.md](performance.md) |

> 在 OAuth、SMTP/COS、预发演练与法务/备案完成前，不得公开上线或宣称生产可用。当前 AI 默认关闭，可在全人工审核模式下运行；生产不得提交任何密钥、连接串、令牌、证书或上传内容。
