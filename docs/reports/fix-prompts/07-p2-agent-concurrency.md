# 阶段 6：Agent、设备、邮件与后台并发

> 先粘贴 `00-master.md`。本阶段覆盖 P2-13～P2-25。AI/MCP 默认关闭，但代码正确性仍要修；不要因为“默认关闭”而跳过。

## 范围

- **P2-13**：两个不同 challenge 并发完成可突破活跃设备上限（计数在事务外）。
- **P2-14**：Agent 预算无原子占位；内容额度会阻断已领取任务。
- **P2-15**：Agent 释放租约用旧实体全列保存，可清除别人刚领取的新租约。
- **P2-16**：MCP 请求超时/取消会结束整个会话，进程仍返回 0。
- **P2-17**：Agent 允许向任意 HTTP 地址发送 Bearer。
- **P2-18**：产物名称 `artifact.Name` 未脱敏。
- **P2-19**：Agent 任务游标截断到毫秒，可分页重复。
- **P2-20**：非法 JSON/未知字段不进失败熔断计数。
- **P2-21**：Device CLI `uname` 超时在同步 `ReadToEnd` 之后，挂起时无效。
- **P2-22**：邮件 outbox 租约无 fencing，旧持有者可覆盖新终态。
- **P2-23**：媒体清理固定最早 N 条，失败对象饿死后续。
- **P2-24**：备份状态不校验新鲜度；健康检查异常误报数据库故障。
- **P2-25**：评论审核通知/邮件缺少目标链接。

## 必读

- 报告对应章节。
- `src/WhalePod.Infrastructure/Services/DeviceVerificationService.cs`
- `src/WhalePod.Infrastructure/Services/AgentModerationService.cs`
- `src/WhalePod.Infrastructure/Services/AiBudgetService.cs`
- `src/WhalePod.Infrastructure/Services/AiSettingsService.cs`、`AiQualityService.cs`
- `src/WhalePod.Agent/*`（McpStdioServer、AgentApiClient、AgentCliOptions、Program）
- `src/WhalePod.DeviceCli/Program.cs`
- `src/WhalePod.Infrastructure/Email/EmailDispatchService.cs`、`EmailSender.cs`
- `src/WhalePod.Infrastructure/Services/MediaCleanupService.cs`
- `src/WhalePod.Infrastructure/Services/BackupStatusProvider.cs`、`SystemHealthService.cs`
- `src/WhalePod.Infrastructure/Services/ContentModerationService.cs`、`NotificationPresentation.cs`、`EmailTemplateRenderer.cs`
- 相关测试：`DeviceVerificationServiceTests`、`AgentHttpTests`、`AiQualityServiceTests`、`DeviceHttpTests`、`MediaCleanupServiceTests`、`SystemHealthServiceTests`

## 实施要求

### A. 设备上限（P2-13）

- 在一个事务内锁定用户行（`FOR UPDATE`）或使用数据库计数约束，再检查活跃设备数并插入/重新激活。
- 不同 challenge、同一账号、名额只剩 1：两个并发完成恰好一个成功。
- 不改变公钥跨账号绑定规则；错误码保持 `DEVICE_LIMIT` 语义。
- 测试：两个 DbContext/连接 + 屏障；最终活跃数 ≤ max。

### B. Agent 预算与租约（P2-14/P2-15）

1. 预算：
   - 用原子占位/结算（条件 UPDATE 或行锁）保证每日任务/token/内容额度不被并发超发；
   - 内容额度只限制“新任务生成”，不得阻断已预留任务的 claim/suggest；
   - 批量生成按剩余额度截断。
2. 租约：
   - 给 `ModerationTask` 增加 `LeaseToken`/`LeaseGeneration`（新迁移）；
   - claim 写入新 token；release/complete/suggest 使用 `WHERE id=@id AND lease_token=@token AND status=...` 原子条件更新；
   - 旧 release 不得清掉新租约或把 suggested 退回 queued。
3. 测试：
   - 额度=1 的并发提交只成功一个；
   - 内容额度=1：生成 1 个任务后可 claim/suggest 完成；
   - release 与新 claim 交错：新租约保持；
   - 旧 release 不能回退终态。

### C. MCP 与 CLI（P2-16/P2-17/P2-21）

1. MCP：
   - 每个请求独立 CTS；区分请求取消、传输超时、进程退出；
   - 单个工具超时不应结束整个 stdio 会话；取消通知应能取消在途调用；
   - 清理逐项限时/容错；致命错误返回非零，正常 EOF 返回 0。
2. Agent HTTP：
   - 默认强制 HTTPS；仅 Development 显式允许回环 HTTP；在发任何请求前校验；
   - 不允许把 Bearer 发往非回环 HTTP。
3. CLI：
   - 异步读取 stdout/stderr + 整体超时；超时终止子进程并降级；
   - 测挂起 `uname`、大量 stderr。
4. 测试：MCP 首个调用超时后下一请求仍可处理；取消传播；API 超时退出码非 0；CLI 在 2 秒内退出。

### D. 脱敏、游标、熔断（P2-18/P2-19/P2-20）

1. 对所有用户可写文本（title/body/artifact.Name/Note/Content/image alt）统一 `RedactInto`；名称/备注/正文同样本断言不保留原文。
2. Agent 任务游标保留实际精度（ticks/微秒）或统一排序精度；遍历无重复、最终结束。
3. 建议提交的 JSON 解析失败（损坏 JSON/未知字段/类型错误）纳入脱敏失败记录/熔断；认证错误不计入。
4. 测试：微秒时间戳 limit=1 遍历；连续 5 次非法 JSON 触发熔断；名称含邮箱/令牌被脱敏。

### E. 邮件与后台（P2-22/P2-23/P2-24/P2-25）

1. Outbox：
   - 增加 `LeaseToken`/代次；发送前/完成后条件更新；
   - 旧持有者不能把新 `sent` 改回 pending/failed；
   - 逐条领取或续租；保持 at-least-once 语义，但不允许终态倒退。
2. 媒体清理：
   - 失败记录 `NextAttemptAt`/退避/隔离；保证后续条目推进；
   - 不做无限重试同一批。
3. 备份/健康：
   - 校验备份状态 JSON 结构、时间范围、最大年龄（如 24h）；组件独立降级；
   - 不要把备份/其他异常统一报成数据库不可达。
4. 评论审核通知/邮件：生成带帖子 ID + 评论锚点的内部链接；保留鉴权；通知/邮件断言目标存在。
5. 测试：双 dispatcher + 可控时钟；最早 N 条永久失败 + 后续正常对象；备份超龄/类型错误；评论通知链接。

## 验收

- 所有并发修复有确定性测试；最终状态/审计/通知一致。
- 全量构建/测试 0 failed、0 skipped。
- AI/MCP 默认关闭行为不变；未引入自动通过/驳回。
- 不新增外部依赖（除非先问）。

## 停止条件

- 需要改数据库结构（LeaseToken/预算表）：先给迁移与回滚方案，问。
- 需要真实 SMTP/模型/MCP 客户端：列为外部前置，用 fake/本地 stub 验证。
- 需要改变 AI 只建议边界：拒绝并问。
- 需要引入分布式锁/Redis：先问；优先数据库条件更新。

## 输出

按 `00-master.md` 的阶段结果格式；更新 `fix-log` 中 P2-13～P2-25。建议按设备、Agent、邮件/后台分 3 个 commit，但不要自动提交。
