# 架构债与后续拆分计划（P3-07/P3-08/P3-10）

本文件记录本轮审查确认的架构债与**后续**处理计划；本轮不进行大重构，避免与安全/并发整改混在一起。

## 1. 巨型文件与重复组合根（P3-07）

现状：

- `src/WhalePod.Web/Program.cs` 约 2,700 行；已抽出 `InteractionEndpoints`、`EditorEndpoints`、`AdminConsoleEndpoints`、`GitHubOAuthEndpoints`、`WebFormGuard`，但剩余端点、认证配置与展示辅助仍集中。
- `src/WhalePod.Infrastructure/Services/AgentModerationService.cs` 约 1,700 行；任务同步、领取/释放、建议、预算、脱敏、详情投影在同一类。
- Web/API 组合根重复注册约 16 种服务。

计划（按顺序，每步独立提交、行为不变）：

1. **端点模块化（Web）**：把 `/posts`、`/comments`、`/admin`、`/settings`、`/devices` 等端点按领域迁到 `Endpoints/` 文件；`Program.cs` 只保留宿主配置与 `MapXxxEndpoints()`。以现有 HTTP 测试作为回归门禁。
2. **共享注册**：抽出 `AddWhalePodCoreServices` / `AddWhalePodWebServices` 扩展，Web/API 复用；逐项对照 DI 差异，避免误共享 scoped/singleton。
3. **Agent 按用例拆类**：`AgentTaskLeaseService`（claim/release/expire/fencing）、`AgentTaskSyncService`（生成/失效）、`AgentSuggestionService`（提交/幂等）、`AgentBudgetGate`（预算/熔断）、`AgentContentProjection`（脱敏/详情）。保持 `AgentModerationService` 作为门面，端点不变。
4. 拆分以测试先行：每个类先移动原逻辑 + 保留现有测试，再逐步加针对性单测；不做“顺手”逻辑改动。

验收：`Program.cs` < 800 行；Agent 服务文件 < 700 行；DI 注册去重；现有全部测试全绿；无公开契约变化。

## 2. Contracts 项目边界（P3-08）

现状：`WhalePod.Contracts` 仅含 `DeviceProtocol`（约 300 行）；HTTP DTO/错误码在 `WhalePod.Infrastructure`，Agent 使用手写 JSON。

决定（本轮）：

- 明确 `WhalePod.Contracts` 的职责为**设备协议与跨进程二进制契约**，不承接 HTTP DTO；在 README/架构文档标注。
- HTTP DTO 继续留在 Infrastructure/Services，由 OpenAPI 契约测试约束（`ApiContractTests`），避免一次性大迁移。
- 后续若需要独立 API 客户端包，再新建 `WhalePod.ApiContracts` 并迁移 DTO；需要单独的兼容性与版本策略评审。

## 3. 公开侧栏与信息流性能（P3-10）

现状：公开侧栏渲染全部启用板块与热门标签；feed 投影完整实体；已有分页，但未对“板块/标签数量增长”做上限。

计划：

1. 以现有种子数据扩容到 200 板块 / 2,000 标签 / 50k 帖，记录 `/` 与 `/tags` 的 SQL 执行计划、查询次数、p95 内存与响应时间（k6 + `EXPLAIN (ANALYZE, BUFFERS)`）。
2. 若侧栏查询 >20ms 或返回 >100 项：增加侧栏上限（如 50）+ “全部板块/标签”页；热门标签按使用量分页。
3. 若 feed 投影成为瓶颈：改为分页投影 + `AsNoTracking` 已验证；必要时只选摘要字段（现有实现已投影），不引入缓存框架。
4. 未经测量不优化；测量报告写入 `docs/design/performance.md` 并附命令/提交。

## 4. 可访问性与浏览器资产（P3-11）

- 新增 `tests/browser/`：无 npm 依赖的 CDP smoke（截图、控制台错误、可选 axe-core 扫描），详见该目录 README。
- 历史 axe 结论的原始报告未留存，acceptance 已标注；后续每次 UI 变更以 `tests/browser/` 脚本产出报告并归档。

## 5. 进度（2026-10-30 迭代 1）

- 端点模块化：`Program.cs` 2,783 → 2,096 行；已抽出 `Endpoints/DiscoveryEndpoints.cs`（RSS/Sitemap/robots）与 `Endpoints/AccountEndpoints.cs`（资料/邮件偏好/注销/设备/申诉/退订），共享 `WebEndpointHelpers`。
- P3-10 测量完成：50k 帖子/200 板块/2,000 标签下首页 p95 36 ms；按阈值增加侧栏 30 项上限与 `/categories` 页，详见 performance.md 第 6 节。
- 迭代 2：`Program.cs` 2,096 → 1,299 行；新增 `Endpoints/AdminConfigEndpoints.cs`（标签/邀请码/审计导出/服务账号/AI/敏感词，约 820 行），`Csv`/凭据 reveal helper 一并迁出。
- 迭代 3：`Program.cs` 1,298 → 785 行；评论创建并入 `InteractionEndpoints`，编辑器 create/preview 并入 `EditorEndpoints`，健康/会话/计数器抽到 `Endpoints/HostEndpoints.cs`，`StatusPageHtml` 移入 `WebEndpointHelpers`。
- 验收状态：`Program.cs` <800 已达成；Web/API 共享注册留作后续可选；`AgentModerationService`（1,723 行）仍未拆分，下一步按第 3 步拆类。
