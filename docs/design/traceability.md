# WhalePod（鲸群）需求追踪矩阵

| 项目 | 内容 |
|---|---|
| 文档版本 | v0.1.0 |
| 编制日期 | 2026-10-27 |
| 需求基线 | [SRS](../SRS.md) v0.1.0 |
| 配套文档 | [architecture.md](architecture.md)、[erd.md](erd.md)、[openapi.yaml](../api/openapi.yaml)、[acceptance.md](acceptance.md)、[performance.md](performance.md)、[device-cli.md](../device-cli.md)、[agent-mcp.md](../agent-mcp.md) |
| 用途 | 将当前交付版本需求映射到数据模型、HTTP 契约、SSR/运维流程；标明本地交付与生产前置条件 |

> 状态口径：**覆盖** = 设计、实现与自动化/浏览器验收有明确落点；**部分** = 主体已实现，仍有功能边界或外部环境依赖；**SSR/运维** = 不进入公开 JSON API，由 Blazor SSR、CLI 或运维流程承担；**外部** = 需要凭据、云资源、法务或真实设备。

## 1. 覆盖摘要

| 项目 | 结果 |
|---|---|
| SRS §5.1 核心实体 | 27/27 已建模 |
| 当前交付版本支撑实体 | `MediaUpload`、`EmailSuppression`、`Appeal`、`ModerationCheck`、`SensitiveWord`；另有 `CopyDedupe` 内部去重表 |
| OpenAPI | 45 路径 / 53 操作 / 86 schema |
| UAT | 20 个场景已建立；执行状态以 [acceptance.md](acceptance.md) 为准（UAT-15 未执行，多项为部分；历史本地结论未随仓库留存原始命令/产物） |
| AI 边界 | 只建议、人工裁决全部；无自动通过/驳回；无公网 MCP |
| 主要生产前置 | 真实 OAuth/SMTP/COS、腾讯云预发与迁移、Q-02 模型/标注、C-09 法务、真实屏幕阅读器/物理设备 |

## 2. 功能需求分组

| REQ 区段 | 模块 | 数据/契约落点 | 状态 |
|---|---|---|---|
| 001–010 | 账号与访问 | User、ExternalLogin；GitHub OAuth SSR、`/activate`、资料/注销/头像；Cookie + CSRF | 部分：OAuth 代码与 fake 测试完成，真实凭据/回调/近期重认证待外部 |
| 011–017 | 邀请与信任 | InviteCode、InviteRedemption、User.trust_level；`/admin/invites`、邀请兑换 SSR | 覆盖：失败兑换仅审计，邀请树/批量停用已实现，导出为后续版本 |
| 020–029 | 板块、帖子与浏览 | Category、Post、PostImage、Tag、PostTag；公开 API/SSR、`/admin/categories`、标签管理 | 覆盖 |
| 032–043 | 编辑与产物 | Post、Artifact、MediaUpload、Version；帖子/评论写 API、上传 API、编辑器预览/草稿 | 部分：上传 UI 仍需粘贴 MediaUpload ID；S3/COS 待预发 |
| 050–057 | 互动 | Comment、PostLike、CommentLike、Report；评论/点赞/采纳/举报 API 与 SSR | 部分：@提及未实现；其余覆盖 |
| 060–069 | 审核与治理 | ModerationAction、ModerationCheck、SensitiveWord、Appeal、AuditLog；人工队列/举报/申诉/敏感词 | 部分：机审供应商未接入；人工治理闭环覆盖 |
| 071–078 | 通知与邮件 | Notification、EmailOutbox、EmailPreference、EmailSuppression；站内通知/邮件 outbox/偏好/退订/保留期 | 部分：真实 SMTP 待外部；管理员手动重发、投诉反馈、目标删除提示待补 |
| 080–086 | 搜索与发现 | Post/Tag/User + pg_trgm；`/search`、`/tags`、`/users`、RSS/Sitemap/robots/SEO | 覆盖；外部搜索索引为后续版本 |
| 090–101 | 设备验证 | Device、DeviceChallenge、AuditLog、Notification；challenge/complete、SSR 设备页、CLI、管理员历史 | 覆盖；macOS/Linux 实机待验证 |
| 110–119 | 管理后台 | 所有治理/账号/邀请/AI 表与 SiteSetting；`/admin/*` 统一后台 | 覆盖；近期重认证未实现 |
| 120–128 | 通用页面与体验 | Blazor SSR；主题三态、错误/空态、skip link、减少动效、键盘与响应式 | 覆盖（本地）；真实屏幕阅读器/物理设备待人工 |
| 129–143 | AI 审核、Agent API 与 MCP | ServiceAccount、ServiceToken、AgentInstance、ModerationTask、ModerationSuggestion、SiteSetting.ai_*；Agent API + stdio MCP | 覆盖（默认关闭）；Q-02/200 样本/真实 MCP 客户端待外部 |

## 3. 关键需求抽查

| 需求 | 落点 | 状态 |
|---|---|---|
| REQ-F-003 邀请激活 | `User.activated_at`、`InviteRedemption`；SSR 兑换与激活门禁 | 覆盖：待激活只能访问激活页，写接口返回 `activation_required` |
| REQ-F-005 账号状态 | `User.status/status_reason/status_expires_at`；管理员用户管理 | 覆盖：禁言/封禁/解封、期限与原因、自我/最后管理员保护 |
| REQ-F-006/075 资料与邮件偏好 | User、EmailPreference、MediaUpload；`/settings/profile`、`/settings/emails`、头像上传/恢复 | 覆盖：头像 WebP 重编码、安全邮件不可关闭、审计不含资料值 |
| REQ-F-007/067 授权与审计 | 服务层授权、AuditLog 只追加；管理端筛选/CSV 导出 | 覆盖：所有权/角色/状态服务端校验，导出 ≤31 天且脱敏 |
| REQ-F-008 只读/关闭登录 | SiteSetting；`/admin/mode`、Web/API 中间件 | 覆盖：只读/关闭登录/成员写策略与审计 |
| REQ-F-011/012/013/114 邀请 | InviteCode 哈希、邀请兑换、后台管理 | 覆盖：明文一次性、批量停用、失败兑换审计（不落表） |
| REQ-F-020/021/022 板块与信息流 | Category/Post 部分索引；`GET /feed`、SSR 首页 | 覆盖：latest/active/unresolved、板块/标签筛选、游标 |
| REQ-F-024/026 帖子详情/可见性 | Post/Artifact/PostImage/Comment；公开详情与 `/me/*` | 覆盖：approved-only 公开，作者/管理员可见性，删除占位 |
| REQ-F-025/115 标签 | Tag/PostTag；公开标签页、`/admin/tags` | 部分：缓存失效未实现；合并/重命名为后续版本 |
| REQ-F-027/054 内容编辑删除 | Post/Comment.Version；If-Match、软删除、采纳回退 | 覆盖 |
| REQ-F-028/053 采纳 | 复合 FK + 部分唯一索引 + IsSolved；accept API | 覆盖：单答案、禁自采纳、删除回退 |
| REQ-F-034/036/038 字段/图片/产物 | 服务层校验、MediaUpload/PostImage、Artifact | 覆盖：上传嗅探/重编码/去元数据，产物纯文本不执行 |
| REQ-F-035/040 编辑器 | `/editor`、服务端预览、本地草稿 | 部分：预览/草稿已实现；图片入口仍为粘贴上传 ID |
| REQ-F-039/057 复制/浏览量 | Artifact/Post.copy_count、Post.view_count；内部计数端点 | 覆盖：10 分钟去重；浏览量进程内去重，多实例需持久化 |
| REQ-F-050/055/064 点赞/举报 | PostLike/CommentLike/Report；API + 管理处理 | 覆盖：幂等、自赞拒绝、举报人隐私、处理动作复用治理服务 |
| REQ-F-060–065 审核/处罚 | 状态机、ModerationAction、User 状态；人工队列 | 覆盖：合法流转、理由、版本冲突、通知/审计、最后管理员保护 |
| REQ-F-068 敏感词/机审 | SensitiveWord、ModerationCheck、SiteSetting | 部分：敏感词策略/词表覆盖；外部机审供应商未接入 |
| REQ-F-071/073/074 通知/邮件 | Notification、EmailOutbox、Preference、Suppression | 部分：站内/邮件 outbox/偏好/退订/保留期覆盖；真实 SMTP 与手动重发待补 |
| REQ-F-080/081 搜索 | pg_trgm + ILIKE；`GET /search`、SSR 搜索 | 覆盖：三类计数、单类型游标、限流/超时；中文基线见 performance.md |
| REQ-F-084–086 RSS/Sitemap/SEO | 静态出口；canonical/OG/JSON-LD | 覆盖：ETag/Last-Modified/304；超 5 万 URL 的 sitemap 索引为后续版本 |
| REQ-F-090–101 设备验证 | Device/Challenge；API、SSR、CLI、管理员历史 | 覆盖：哈希挑战、一次性、失败锁定、设备上限、撤销/审计/通知 |
| REQ-F-110–119 管理后台 | `/admin/*`、AdminOnly、SiteSetting | 覆盖：统一入口、审核/举报/用户/分类/标签/邀请/AI/审计/健康/模式 |
| REQ-F-121/123 主题与可访问性 | SSR/前端；主题三态、skip link、焦点、减少动效 | 覆盖（本地）：axe/键盘/设备仿真通过；真实屏幕阅读器待人工 |
| REQ-F-129–143 AI/Agent/MCP | 任务/租约/建议/心跳/策略；Agent API + stdio MCP | 覆盖（默认关闭）：最小 scope、ContentHash、预算/熔断、人工裁决；Q-02 待外部 |

## 4. 外部接口

| 需求 | 落点 | 状态 |
|---|---|---|
| REQ-I-001 GitHub OAuth | ExternalLogin + SSR OAuth | 代码覆盖；真实 App 凭据/回调/近期重认证待外部 |
| REQ-I-002 事务邮件 | EmailOutbox/Preference/Suppression | 本地 Logging 覆盖；真实 SMTP 待外部 |
| REQ-I-003 对象存储 | MediaUpload/PostImage；Local/S3 适配器 | Local 已验证；COS/S3 预发待外部 |
| REQ-I-004 内容安全 | ModerationCheck + 人工转审 | 敏感词策略覆盖；供应商待接入 |
| REQ-I-005 设备 CLI | challenge/complete + 附录 A | 覆盖；Windows 实测，Linux/macOS 待实机 |
| REQ-I-006 公开 API | OpenAPI 3.1、统一包络、游标、CSRF | 覆盖 |
| REQ-I-007 RSS/Sitemap | 非 JSON 出口 | 覆盖 |
| REQ-I-008 监控备份 | `/healthz`、结构化日志、备份脚本 | 本地覆盖；生产监控/异地备份待运维 |
| REQ-I-009 Agent API | `/agent/*`、Bearer、scope、租约、no-store | 覆盖 |
| REQ-I-010 本机 stdio MCP | 非 HTTP；7 工具、版本化 Schema | 覆盖；真实 MCP 客户端 + 模型 E2E 待外部 |

## 5. UAT 与验收

UAT-01～20 的逐项结果、自动化证据、UI/可访问性矩阵与生产前置条件统一维护在 [acceptance.md](acceptance.md)。本矩阵只保留需求到实现的映射，不重复记录批次执行日志。

## 6. SSR/运维承担的当前交付版本行为

| 行为 | 需求 | 说明 |
|---|---|---|
| OAuth 登录、激活、邀请兑换 | REQ-F-002/003/010/012 | Blazor SSR；不进入公开 JSON API |
| 资料、公开主页、邮件偏好、注销、退订、申诉 | REQ-F-006/009/066/075/139 | SSR 表单 + 服务层；权限/CSRF/审计/通知不因“仅页面”豁免 |
| 标签/邀请/服务账号/AI/系统健康/设备历史 | REQ-F-012/025/101/114–119/136/143 | 管理后台 SSR；不在公开 API 暴露 |
| 备份、监控、发布回滚、法务与备案 | REQ-NF-014/015/019、C-09 | 运维/法务流程；见 production-readiness.md |
| 复制/浏览内部计数 | REQ-F-039/057 | Web 内部端点，不进入公开 OpenAPI |
| 机审原始结果/供应商状态 | REQ-F-063 | 供应商接入后补齐；当前人工审核可用 |

## 7. 维护规则

- 修改端点、实体或 SSR 行为时，同时更新本矩阵、OpenAPI/ERD 与相关验收记录。
- SRS 变更先更新 REQ 编号与验收标准，再更新本矩阵；本矩阵不反向修改 SRS。
- 不把“SSR 承担”当作“无需安全控制”：SSR 流程同样要求服务端授权、CSRF、审计、错误码与自动化测试。
- 新增后续版本功能只在 SRS 第 8 章登记，不进入 当前交付契约；生产启用前置条件变化时更新 acceptance/production-readiness。
