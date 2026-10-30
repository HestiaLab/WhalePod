# 阶段 5：会话、匿名入口、Web 表单与业务状态机

> 先粘贴 `00-master.md`。本阶段覆盖 P2-01～P2-12，以及 P3-01/P3-02/P3-09。建议按“会话/入口 → Web 表单 → 业务状态”三个小批次执行，每批次全量回归。

## 范围

### 会话与匿名入口

- **P2-01**：退出登录只清客户端 Cookie；旧 Cookie 在 API 仍有效。
- **P2-02**：API `/api/v1/search` 没有搜索限流/超时，可绕过 Web 30 次/分钟与 3 秒超时。
- **P2-03**：非法游标（负数/超范围 ticks）触发 500。
- **P3-01**：正常页面没有退出登录入口，只有 `/activate` 表单。
- **P3-02**：封禁后旧 Cookie 仍可读取自己的 pending/rejected 详情。
- **P3-09**：8 小时滑动 Cookie 无绝对过期。

### Web 表单

- **P2-04**：提交失败不保留输入（评论无草稿；发帖仅 JS 本地草稿可手动恢复）。
- **P2-05**：评论分页改变回复归属；深度 >6 的回复被静默截断。
- **P2-06**：危险后台操作依赖 JS `confirm`，无 JS 时直接执行；“快速通过”无确认。

### 业务状态

- **P2-07**：已采纳评论编辑进入 pending / 被驳回后，父帖仍 solved。
- **P2-08**：申诉复核说明允许 1,000 字，但写入 `varchar(500)` 的 ModerationAction.Reason。
- **P2-09**：申诉目标删除后无法结案；队列固定最旧 50 条，可被堵塞。
- **P2-10**：敏感词“命中即拒绝”只存配置，无消费者。
- **P2-11**：`LastActivityAt` 不随评论/采纳更新。
- **P2-12**：板块显示名硬编码决定“抄作业”必须填内核。

## 必读

- 报告对应章节。
- 会话/入口：`src/WhalePod.Web/Program.cs`（认证/Cookie/CSRF/logout/搜索限流）、`src/WhalePod.Api/Program.cs`（搜索/认证）、`src/WhalePod.Infrastructure/Services/PublicContentService.cs`、`PublicSearchService.cs`、`ActorGuard.cs`
- Web 表单：`PostDetail.razor`、`Editor.razor`、`Components/App.razor` 草稿 JS、`AdminConsoleEndpoints.cs`、`Components/Pages/Admin/Moderation.razor`、`ModerationDetail.razor`
- 业务状态：`CommentWriteService.cs`、`ContentModerationService.cs`、`AppealService.cs`、`SensitiveWordAdminService.cs`、`PostWriteService.cs`、`CategoryAdminService.cs`、`PublicContentService.cs`
- 现有测试：`ApiProtectionTests`、`WebProtectionTests`、`PublicPageHttpTests`、`CommentWriteServiceTests`、`AppealServiceTests`、`AdminUserServiceTests`、`SensitiveWordAdminServiceTests`

## 实施要求

### A. 会话撤销（P2-01/P3-09）

1. 选择最小方案：给 `User` 增加 `SessionVersion`（或复用现有安全戳；不要引入 Redis/会话表，除非必要），登录时写入 Cookie claim，Web/API 认证后查库比对；logout 递增版本。
2. 退出后旧 Cookie：
   - 访问 Web 受保护页面 → 401/重定向登录；
   - 访问 API 受保护端点 → 401；
   - 公开读不受影响。
3. 不要破坏 Web/API 共享 Cookie/Data Protection；更新 `ClaimsIdentity` 与现有登录（OAuth/开发登录）路径。
4. 绝对过期：在 Cookie 认证上增加 `ExpireTimeSpan` 之外的绝对截止 claim/校验（或至少产品明确的策略）；不要只改滑动窗口。
5. 测试：旧 Cookie 重放、其他设备会话是否保留（按产品策略）、logout 后重新登录、8 小时滑动行为。

### B. API 搜索保护与游标（P2-02/P2-03）

1. 为 `/api/v1/search` 挂与 Web 共享的搜索限流策略（30/min 可配置）与查询超时（3s 可配置）；不要另起一套不一致的数字。
2. 超时取消数据库查询；429 返回 `Retry-After`/`retryAfterSeconds`。
3. 游标解码显式校验 ticks 在 `DateTimeOffset` 合法范围；非法返回 400，不抛未捕获异常。
4. 测试：35 次 API 请求出现 429；Web/API 限额一致；负数/超大 ticks、错误 base64、scope 不匹配 → 400。

### C. 输入保留与评论分页（P2-04/P2-05）

1. 发帖/评论失败：服务端回填原值（安全、用户隔离；不要把内容放 URL）；评论也要有本地草稿或服务端暂存；无 JS 也保留。
2. 错误字段定位/聚焦；保留已选标签、图片 ID、产物。
3. 评论回复：独立读取目标评论（同帖、可见、未删除），或在分页链接保留回复上下文；目标失效明确报错，不静默变根评论。
4. 深层回复：视觉缩进可限制，但内容不能截断；为第 7 层及更深提供可达展示（平铺/折叠/“查看上下文”）。
5. 测试：51 条评论第二页回复的 `ParentId` 正确；8 层回复每条可见；失败后字段保留。

### D. 无 JS 确认（P2-06）

1. 为审核通过/驳回/隐藏/删除、用户处罚、快速通过等危险操作增加服务端确认字段/确认页；无 JS 直接 POST 且缺确认 → 拒绝。
2. 保留现有 CSRF；确认字段不是替代 CSRF。
3. 测试：无 JS/缺确认 → 状态不变；确认后正常执行；版本冲突仍拒绝。

### E. 业务状态（P2-07～P2-12）

1. **P2-07 采纳**：评论离开 Approved（编辑→Pending、Reject）时，事务性清除 `Comment.IsAccepted`、`Post.AcceptedCommentId/IsSolved`，写审计/通知；明确再次 Approved 是否恢复采纳（默认不恢复，需重新采纳）。
2. **P2-08 申诉长度**：统一 500/1000 契约，扩列或限制；返回明确 400，不落库异常；测 500/501/1000/1001。
3. **P2-09 申诉队列**：目标/申诉人删除时支持“撤回/失效结案”或允许注明后驳回；队列分页/游标，避免最旧 50 条堵塞。
4. **P2-10 敏感词**：接入创建/编辑/审核路径，记录命中词/策略版本；人工兜底不变；如果短期不实现，删除“命中即拒绝”选项并明确标注（需产品确认）。不要引入自动公开。
5. **P2-11 活跃时间**：评论批准、采纳/取消时事务更新 `LastActivityAt`；待审评论不顶帖；并发不回退。
6. **P2-12 板块规则**：用稳定 Slug/ID 或显式策略字段决定 kernel 必填；改显示名不影响规则；迁移现有“抄作业”配置需问。

## 验收

- 会话：旧 Cookie 退出后 401；API/Web 一致；限流一致；游标 400。
- 表单：失败输入保留；评论第二页回复正确；深层可见；无 JS 危险操作需确认。
- 业务：采纳状态一致；申诉长度/队列闭环；敏感词策略真实生效或明确撤下；活跃排序更新；板块改名不改规则。
- 全量构建/测试 0 failed、0 skipped；浏览器验证 375/1280 + 无 JS。

## 停止条件

- 会话方案需要外部存储/大改认证：先说明并问。
- 敏感词“reject”是否实现：先问。
- 采纳再次批准是否恢复：先问产品。
- 板块规则迁移影响现有数据：先问。
- 任何放宽 CSRF/限流的做法：拒绝。

## 输出

按 `00-master.md` 的阶段结果格式；更新 `fix-log` 中对应 ID。建议分 3 个 commit（会话/入口、Web 表单、业务状态），但不要自动提交。
