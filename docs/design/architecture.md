# WhalePod（鲸群）架构设计

| 项目 | 内容 |
|---|---|
| 文档版本 | v0.1.0 |
| 编制日期 | 2026-10-27 |
| 需求基线 | [SRS](../SRS.md) v0.1.0（唯一事实来源） |
| 数据模型 | [erd.md](erd.md) |
| HTTP 契约 | [openapi.yaml](../api/openapi.yaml) |
| 验收与限制 | [acceptance.md](acceptance.md)、[production-readiness.md](production-readiness.md)、[architecture-debt.md](architecture-debt.md) |

本文记录当前交付版本的系统边界、运行时拓扑、关键设计决策与实现约束。具体需求行为以 SRS 为准；表结构与索引以 ERD/迁移为准；HTTP 字段以 OpenAPI 为准。

## 1. 系统上下文

WhalePod 是单站、邀请制、面向 AI 工具使用者的技术社区。核心内容是可复现的实测记录与可直接复制的文本产物，信任来自人工审核、账号状态与 Ed25519 设备验证。

```text
Browser ──HTTPS──> Web (Blazor Web App, SSR + 按需交互)
   │                    │
   │                    ├─ PostgreSQL（业务数据、审计、发件箱、任务）
   │                    ├─ 本地媒体目录 / S3 兼容对象存储
   │                    └─ SMTP（业务事务只写 outbox，后台 worker 投递）
   │
   └──同站 /api/v1──> API (Minimal API, 公开读/写 + Agent API)
                        ├─ PostgreSQL（与 Web 共享）
                        ├─ 媒体存储
                        └─ 后台 worker（邮件、媒体、challenge、保留期）

管理员本机 Agent ──出站 HTTPS / stdio MCP──> API /agent/*
```

- Web 与 API 必须同站部署（同 host，可不同端口，或同域反向代理），共享 Data Protection 密钥目录、会话 Cookie 与 CSRF Cookie。
- API 进程运行后台 worker；Web 不运行外部发送/清理任务。
- Agent 只发起出站 HTTPS；MCP 只走 stdio，不监听公网端口。

## 2. 解决方案结构

| 项目 | 职责 |
|---|---|
| `WhalePod.Web` | Blazor Web App；公开 SSR 页面、登录/激活、编辑器、设置、后台管理；表单 CSRF/同源校验；静态资源 |
| `WhalePod.Api` | 公开 JSON API、写 API、管理员 API、Agent API；Bearer 服务令牌；限流、请求体上限、后台 worker |
| `WhalePod.Domain` | 实体、枚举、状态机、领域规则 |
| `WhalePod.Contracts` | DTO、错误码、协议常量、跨项目契约 |
| `WhalePod.Infrastructure` | EF Core/PostgreSQL、媒体存储、邮件、Markdown 清洗、验签、审计、业务服务 |
| `WhalePod.DeviceCli` | 本机 Ed25519 设备签名 CLI；不联网、不读取站点令牌 |
| `WhalePod.Agent` | 本机 AI 审核 Agent 与 stdio MCP Server；默认关闭 |
| `tests/WhalePod.Tests.*` | 单元、真实 PostgreSQL/HTTP 集成、E2E 占位 |

## 3. 核心数据与一致性规则

- **主键**：应用生成 UUIDv7；避免自增 ID 枚举，并保持索引局部性。
- **时间**：`timestamptz`，UTC 存储，界面按 `Asia/Shanghai` 展示；后台按北京时间解析的到期时间在服务边界转 UTC。
- **枚举**：`text + CHECK`，便于迁移期追加；API 枚举与 SRS 附录 B/C 同步。
- **版本与并发**：`Post.Version`/`Comment.Version` 作为并发令牌和 ETag；编辑/删除使用 `If-Match`，内容或审核状态变化递增版本。
- **软删除与匿名化**：帖子/评论软删除；用户注销原地匿名化并清空外部身份；审计、举报、审核记录不物理删除。
- **审计**：`AuditLog` 只追加，数据库触发器阻止 UPDATE/DELETE；只记录操作者、动作、目标、原因、请求 ID 与脱敏元数据，不存正文、邮箱、令牌、原始 IP。
- **通知/邮件**：业务事务内写 `Notification` 与 `EmailOutbox`；worker 使用租约、退避与抑制表；邮件为 at-least-once，不在请求事务内调用 SMTP。
- **多态关联**：`Report`、`ModerationAction`、`ModerationTask` 的目标与审计 actor/target 由服务层校验，不建跨表外键。
- **公开可见性**：公开出口只读取 `moderation_status='approved' AND deleted_at IS NULL`；作者可见自己的待审/驳回内容，管理员可见全部；所有公开查询显式过滤，不依赖全局查询过滤器。
- **搜索与游标**：游标包含排序键与 UUID tiebreaker；公开列表只返回 approved 内容。

## 4. 身份、会话与授权

- **登录**：GitHub OAuth（最小 scope、state、已验证邮箱、可选 login/组织白名单）或 Development 开发登录；首次登录创建待激活账号，兑换邀请码后进入成员状态。
- **会话**：`whalepod_session` HttpOnly Cookie；Web/API 共享 Data Protection 密钥目录；CSRF Cookie 为 `whalepod_csrf`，所有非安全方法要求同源与 `X-CSRF-Token`。
- **授权**：每个写请求在服务端校验登录、角色、账号状态（active/muted/banned/deleted）、激活状态与资源所有权；管理员权限每次查库，不信任 Cookie role。
- **管理员引导**：`Auth:BootstrapAdminHandles` 在登录时一次性提升并写审计；生产通过秘密存储注入。
- **站点模式**：只读模式与关闭新登录由 SiteSetting 控制；只读默认阻断写操作，可配置为仅允许已认证成员写；Agent 服务令牌始终阻断。变更写审计。
- **CORS**：仓库不启用 CORS；Web/API 必须同站或同域反向代理，禁止通配来源。

## 5. 内容、审核与治理

- 帖子/评论创建默认 `pending`；管理员人工通过、驳回、隐藏、恢复、升级或删除；驳回/隐藏/删除/升级必须填写原因。
- 审核动作写 `ModerationAction`、审计、作者通知与邮件；`escalate` 保持待审并提升队列优先级。
- 举报支持帖子、评论与用户；管理员处理举报可复用内容动作或账号处罚；举报人身份仅管理员可见。
- 申诉支持被驳回内容与账号处罚；每目标最多一条 pending，禁止自审；通过后恢复内容/解除处罚，写 `appeal_result` 通知与邮件。
- 敏感词策略支持“转人工/拒绝”；机审结果模型已建模，外部内容安全供应商接入属于部署扩展项。
- 管理后台统一入口覆盖审核、举报、用户、邀请码、板块、标签、AI、服务账号、申诉、审计、系统健康与站点模式；危险操作二次确认。

## 6. 设备验证

- CLI 在本机生成/保存 Ed25519 私钥，按 `community-device-v1` 对一次性 challenge 签名；私钥不上传、不打印、不写入日志。
- 服务端只保存 challenge 哈希；默认 5 分钟一次性使用，失败不消耗；验签与设备绑定在同一事务。
- 设备徽章按活跃设备重算；默认 5 台活跃设备上限；连续失败短时锁定；成员/管理员撤销写审计与通知。
- 管理员设备历史只读展示设备与脱敏审计摘要，不返回公钥、私钥、令牌、邮箱、原始 IP 或完整元数据。

## 7. AI 审核 Agent 与 MCP

- AI/MCP 默认关闭；关闭时 `/agent/*` 返回 `503 AGENT_DISABLED`，MCP Server 不启动；当前版本不提供自动通过/驳回。
- 服务账号只持有 `moderation:read`、`moderation:suggest`、`agent:heartbeat` 最小 scope；令牌只存 SHA-256 哈希，可配置精确 IP allowlist。
- Agent 通过出站轮询领取任务；租约默认 10 分钟，任务带 `ContentHash`/内容版本；内容变化使旧任务失效。
- 建议只写 `ModerationSuggestion`，内容保持 pending；人工裁决后才能改变状态；建议与人工动作关联用于一致率统计。
- 每日 token/任务量/内容量预算、服务账号 token 预算覆盖、质量阈值告警与失败熔断已接入；超限返回 429/503 并写审计。
- 本机 MCP 只提供 7 个受控工作流工具，无 shell、文件系统、任意 HTTP 或管理工具；本机模型选型与 200 条标注评估属于启用前置条件（Q-02）。

## 8. 媒体、搜索与发现

- 图片上传服务端按内容嗅探 JPG/PNG/WebP/GIF，限制大小/尺寸/帧数，统一重编码为静态 WebP 并去除元数据；拒绝 SVG/可执行文件。
- Development/测试使用本地媒体目录；非 Development 强制 S3 兼容对象存储；临时/已删除上传由 worker 清理，失败保留数据行重试。
- 搜索使用 PostgreSQL `ILIKE` + `pg_trgm` 索引，公开 `/search` 支持 posts/users/tags 分组计数与单类型游标；SSR 搜索默认 30 次/分钟、3 秒超时。
- RSS/Sitemap/robots 为静态出口，支持 ETag/Last-Modified/304；页面输出 title/description/canonical/OG/Twitter/JSON-LD。
- 中文搜索在本机 3,599 条 approved 帖子规模下 <10 ms；更大数据量需要预发复测或切换 UNION/外部索引，详见 [performance.md](performance.md)。

## 9. 关键设计决策

| 编号 | 决策 | 影响 |
|---|---|---|
| D-A | 全表 UUIDv7 主键 | 防枚举、支持游标；应用生成与索引维护 |
| D-B | 字符串枚举 + CHECK | 迁移易回滚；需维护约束与 API 枚举同步 |
| D-C | `Version` 作为内容版本 + ETag | 并发编辑与 Agent 版本校验；审核状态变化也递增 |
| D-D | 帖子/评论软删除，用户原地匿名化 | 保留引用与审计；注销后外部身份可空 |
| D-E | 仅记录成功邀请兑换 | 失败尝试写审计；避免伪造邀请码外键 |
| D-F | challenge 哈希 + 条件更新一次性使用 | 防重放；事务内绑定设备 |
| D-G | 通知 DedupeKey + 邮件发件箱租约 | 防重复通知/发信；失败可恢复 |
| D-H | AI 任务按内容版本唯一活跃；建议只写建议 | 符合“只建议、人工裁决全部” |
| D-I | OpenAPI 不提供 `/mcp` 与 AI 裁决动作 | MCP 仅 stdio；AI 不改变内容状态 |
| D-J | 内容创建 201 + `moderationStatus` | 统一成功包络；不使用 202/`MODERATION_REQUIRED` |
| D-K | `MediaUpload` 支撑实体 | 解决上传先于发帖的时序、所有权与清理 |
| D-L | Agent 任务绑定 `X-Agent-Instance-Id` | 防跨实例操作与只读令牌占队列 |
| D-M | 评论编辑/删除进入 API | 覆盖作者/管理员权限、软删除与采纳回退 |
| D-N | 通知按时间桶去重、邮件按收件人去重 | 避免永久去重与跨收件人误抑制 |

## 10. PostgreSQL / EF Core 约束

1. 迁移前创建 `citext`、`pg_trgm`；托管库扩展权限受限时，标签唯一索引改用 `lower(name)`，搜索改用 `to_tsvector` 或外部索引。
2. `Post.AcceptedCommentId` 与 `Comment.PostId` 构成循环，需要可延迟外键或分步迁移；`Comment.ParentId` 使用可延迟自引用。
3. 并发令牌与版本条件必须在同一事务递增；ETag 与 Agent 内容版本当前共用 `Version`，审核状态变化会增加 409 概率。
4. 信息流、未读通知、审核队列、租约、活动设备、challenge、邀请码依赖部分索引，索引需含 UUID tiebreaker 以配合游标。
5. `PayloadJson`/`MetadataJson`/`RiskHintsJson`/`TokenUsage`/`RawResponse` 使用 `jsonb`，只作展示/审计负载，不存令牌、私钥、完整 IP 或举报人明文。
6. 审计只追加触发器与受限应用角色必须在迁移中保留；保留期清理通过受控运维角色执行。
7. 通知/计数/发件箱使用原子更新或事务内联更新；worker 使用 `FOR UPDATE SKIP LOCKED` 或租约。
8. 种子数据只包含板块、标签与 SiteSetting 默认值；AI 默认关闭、只读关闭；不播种真实用户、邮箱、令牌或邀请码。

## 11. 安全与隐私不变量

- 仓库不保存连接串、SMTP/OAuth/COS 密钥、服务令牌明文、设备私钥或生产 Data Protection 密钥。
- 生产必须配置 `Audit:IpHashSecret`、`ViewCounter:DedupeSecret`、`CopyCounter:DedupeSecret`、邮件密钥与 Data Protection 持久目录；缺失启动失败。
- 反向代理只在显式 `KnownProxies`/`KnownNetworks` 下启用 Forwarded Headers；审计只存有效客户端地址 HMAC。
- 日志不记录原始 IP、邮箱、令牌、正文或完整上传路径；请求日志使用 HMAC `userRef`。
- 公开 API 不返回邮箱、GitHub ID、举报人、设备公钥/私钥、服务令牌或用户删除内容。
- GitHub ID HMAC 黑名单默认关闭，启用前必须完成法务确认与保留期评审，见 [privacy-github-hmac.md](privacy-github-hmac.md)。

## 12. 交付范围与外部前置

当前代码与本地验收已完成；以下项目依赖外部环境或业务决策，不影响本地交付，但在公开生产启用前必须完成：

- 真实 GitHub OAuth App 凭据、回调域名与近期重认证验收。
- 真实 SMTP、COS/S3、CDN/WAF 与腾讯云预发/生产迁移、备份恢复、回滚演练。
- Q-02 本地模型选型、200 条标注样本质量评估、真实 MCP 客户端 + 模型 E2E。
- C-09 法务确认、ICP/公安备案、密钥轮换与日志保留方案。
- 真实屏幕阅读器/物理设备 UAT、macOS/Linux CLI 实机与代码签名/公证。
- 完整 10 分钟容量压测与生产监控阈值校准。

详细验收状态见 [acceptance.md](acceptance.md)，上线配置与回滚见 [production-readiness.md](production-readiness.md)、[architecture-debt.md](architecture-debt.md)。
