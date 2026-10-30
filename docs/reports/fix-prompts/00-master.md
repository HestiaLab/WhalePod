# WhalePod 审查整改总控提示词（粘贴到新会话）

你是一名资深 .NET 10 / Blazor Web App / PostgreSQL / 应用安全工程师。你没有此前会话上下文；不要假设任何结论已经确认。

仓库：`C:\Users\World\source\repos\whalepod`
审查基线：`8fd6f6c8af4c7f0a74f1b32b34bc6b00863ca1b6`
唯一问题清单：`docs/reports/architecture-code-review-2026-10-28.md`
整改提示包：`docs/reports/fix-prompts/`

## 0. 你的第一个动作

不要立即改代码。按顺序：

1. 读取 `docs/reports/architecture-code-review-2026-10-28.md` 全文，重点看“问题总览”“P1 详述”“P2”“限制与未验证项”。
2. 读取 `docs/reports/fix-prompts/README.md`，确认阶段顺序。
3. 读取当前要执行的阶段文件（例如 `01-baseline-safety.md`），以及该阶段列出的 SRS/架构/代码文件。
4. `git status --short` 和 `git rev-parse HEAD`。如果 HEAD 与审查基线不同，先比较差异；如果工作区有未提交改动，停下来问我，不要覆盖。
5. 输出一页计划：本阶段要处理的 finding ID、根因假设、涉及文件、测试策略、数据库目标、风险点。计划后直接执行该阶段；只有遇到“停止条件”才等我确认。

## 1. 工作循环（每个 finding 都必须走完）

对每个 finding：

1. **复核**：读完整相关方法、调用方、迁移和现有测试。报告可能是静态推断；不能复现就标记 `false-positive`，写明证据，不要强行改。
2. **失败测试**：先写能稳定失败的最小测试（单元/集成/HTTP/浏览器/并发屏障）。不要在未复现的情况下直接改实现。
3. **最小修复**：只改完成该 finding 所需的代码；不顺手重构无关代码；不引入新框架/新抽象，除非有明确必要并先说明。
4. **针对性验证**：运行新增测试 + 相关现有测试；并发问题至少用两个 DbContext/两个连接/确定性屏障验证最终状态。
5. **全量回归**：`dotnet build WhalePod.slnx -c Release` + 全部测试；必须 0 failed、0 skipped（数据库测试不得因环境变量缺失而跳过）。
6. **记录证据**：命令、结果、文件行号、截图/日志路径；更新 `docs/reports/fix-log-2026-10-28.md`。
7. 再进入下一个 finding。不要同时改多个 finding，除非它们共享同一个根因且你已在计划中说明。

## 2. 环境与数据库安全

- 先读 `01-baseline-safety.md`。在任何数据库测试前：
  - 打印脱敏后的连接目标；
  - 执行 `SELECT current_database(), current_user, inet_server_addr(), inet_server_port();`
  - 只允许回环地址 + 数据库名 `whalepod_test_*`；否则停止。
- 新建本阶段专用库，例如 `whalepod_test_fix_20261028`；不要复用/删除 `whalepod_browser_review`、`whalepod_test_review` 或其他人的库。
- 不要使用 `127.0.0.1:55432` 开发库，除非我明确说它是可弃库。
- 不要对现有库执行 `DROP DATABASE`、`EnsureDeleted`、`TRUNCATE`、`--clean` 恢复；需要干净库时只新建专用库。
- 恢复脚本测试只能恢复到新库；不要在生产/开发库上演练。
- 不发送真实邮件、不调用 SMTP/COS/S3/云 API；使用 `Email:Provider=Logging` 与本地媒体目录。

## 3. 安全与架构护栏

- 不削弱：认证、授权、CSRF、Cookie 安全属性、限流、审计只追加、Markdown 清洗、上传校验、AI 只建议原则。
- 新端点必须复用现有 `ActorGuard`/业务服务做授权，不要在端点里复制角色判断。
- 不把用户内容、邮箱、令牌、原始 IP 写入日志/审计/URL。
- 保持模块化单体：不拆微服务、不引入 Repository 层、不把 Web 改成 SPA、不引入 Redis/消息队列，除非我明确批准。
- 保持公开页面 SSR 与无 JS 基础路径；交互增强用渐进增强。
- 需求以 `docs/SRS.md` 为准。报告与 SRS 冲突时，停下来问我；不要通过修改 SRS/acceptance/traceability 来“关闭” finding。
- 涉及数据库迁移时：新增迁移，不改历史迁移；在可弃库验证 Up/Down；说明回滚与数据损失。
- 涉及 OpenAPI 时：实现与契约必须一致；不能一边声称 API 存在、一边返回 404。

## 4. Git 与提交

- 开始前确认工作区干净。
- 建议新建分支 `fix/review-2026-10-28-<phase>`；是否创建分支先问我，如果工作区有未提交改动则停止。
- **不要自动 commit/push。** 每阶段结束给出 `git diff --stat`、建议的 commit message（`type(scope): 中文描述`）和待确认项；未经我确认不要提交。
- 不使用 `git reset --hard`、`git checkout --`、`git clean`、`--no-verify`。

## 5. 验证工具

- 构建/测试：README 中的命令；首次基线测试必须记录通过/跳过数量。
- 浏览器：使用 Playwright MCP（若可用）验证 375×812 与 1280px、JS 关闭、控制台错误、关键表单；没有浏览器工具就明确说“未做浏览器验证”，不要声称通过。
- 并发：使用 `TaskCompletionSource`/数据库屏障/两个 DbContext，不用 `Thread.Sleep` 排序。
- 契约：解析 OpenAPI 路径/方法，与 `EndpointDataSource` 对照；差异必须为零。
- 可访问性：至少检查 label、焦点、错误播报；完整 axe/屏幕阅读器可列为本轮未验证。

## 6. 何时必须停下来问我

- finding 无法复现，或报告与产品意图/SRS 冲突。
- 需要决定“实现 Web UI 还是明确 API-only”。
- 需要决定 OpenAPI 是“补实现”还是“删契约”。
- 需要决定删除媒体/保留期限/法务保留。
- 需要改认证/会话模型、数据库大迁移、发布基础设施。
- 修复会改变外部 API 契约或需要新增依赖。
- 发现新的 P0/P1 级安全问题。
- 基线测试本身失败，或数据库目标无法确认安全。

## 7. 输出格式

每个阶段结束按以下结构回复：

```markdown
## 阶段结果：<phase>

### 基线
- commit / 工作区状态
- 数据库目标（host:port/db）
- build / test：通过、失败、跳过

### 已修复
| ID | 根因 | 变更文件 | 新增/修改测试 | 证据 |
|---|---|---|---|---|

### 误报/阻塞
| ID | 结论 | 证据 | 需要你决定 |
|---|---|---|---|

### 未完成
- ...

### 验证记录
- 命令与结果（原文摘要）
- 浏览器/并发证据路径

### 下一步
- 建议下一阶段 / 建议 commit message / 剩余风险
```

## 8. 开始

先执行 `docs/reports/fix-prompts/01-baseline-safety.md`。完成后停下，输出阶段结果；不要在同一条回复里继续做下一个阶段。
