# WhalePod 审查整改提示词包

本目录把 [架构与代码质量审查报告](../architecture-code-review-2026-10-28.md) 的整改工作拆成可独立执行的阶段。**建议每个阶段新开一个会话**，避免上下文污染和一次性改动过大。

## 1. 使用方式

推荐流程：

1. 新开会话，粘贴 [00-master.md](00-master.md) 的全文。
2. 告诉新会话：`执行 docs/reports/fix-prompts/01-baseline-safety.md`。
3. 该阶段完成后，让新会话输出交接摘要，再新开会话执行下一个阶段。
4. 不要在同一会话里连续做多个大阶段；如必须复用，要求它先压缩上下文并重新读报告。

也可以直接把某一阶段文件全文粘贴到新会话；每个阶段文件都包含前置阅读、范围、验收和停止条件。

## 2. 阶段顺序

| 顺序 | 文件 | 覆盖问题 | 目标 |
|---|---|---|---|
| 0 | [01-baseline-safety.md](01-baseline-safety.md) | P1-07 的测试库守卫部分 | 建立安全基线；修复测试库误删风险；跑通真实数据库测试 |
| 1 | [02-p1-activation-interactions.md](02-p1-activation-interactions.md) | P1-01、P1-02 的互动/编辑部分 | 激活页资源、编辑重提、点赞/采纳/举报/评论删除 |
| 2 | [03-p1-editor-completeness.md](03-p1-editor-completeness.md) | P1-02 的上传/产物/标签部分 | 图片上传 UI、5 份产物、标签分页 |
| 3 | [04-p1-identity-state-media.md](04-p1-identity-state-media.md) | P1-03、P1-04、P1-05、P1-06 | OAuth 管理员引导、申诉/设备状态机、注销媒体生命周期 |
| 4 | [05-p1-data-contract.md](05-p1-data-contract.md) | P1-07 的恢复脚本部分、P1-08 | 恢复脚本原子性/SSL、OpenAPI 契约对齐 |
| 5 | [06-p2-session-web-business.md](06-p2-session-web-business.md) | P2-01～P2-12、P3-01/02/09 | 会话撤销、搜索限流、游标、表单保留、评论分页、业务状态 |
| 6 | [07-p2-agent-concurrency.md](07-p2-agent-concurrency.md) | P2-13～P2-25 | 设备上限、Agent 预算/租约、MCP、CLI、outbox、清理、健康检查 |
| 7 | [08-p2-ops-test-release.md](08-p2-ops-test-release.md) | P2-26～P2-32、P3-03～P3-12 | 健康检查、迁移回退、E2E/生产启动矩阵、锁文件、负载、发布链、加固 |

**不要跳过 0 阶段。** 该阶段先确认数据库可弃、修复测试库守卫，否则后续任何“真实数据库测试”都可能落到错误目标上。

## 3. 关键原则

- **报告是假设，不是事实。** 每个问题先在代码/测试中复核；不能复现就标 `false positive`，不要为了“修完清单”强行改代码。
- **一次只做一个 finding。** 每个 finding 走：复核 → 失败测试 → 最小修复 → 针对性与全量回归 → 证据。
- **不自动 commit/push。** 每阶段结束给 diff 摘要和建议的 commit message；未经用户确认不要提交。
- **不修改 SRS/验收状态来“让问题消失”。** 如果产品意图与报告冲突，停下来问；需要改需求时单独记录。
- **不削弱安全控制**：CSRF、Cookie、权限、限流、审计、Markdown 清洗、上传校验、AI 只建议原则。
- **不做无关重构**：不引入微服务、Repository 层、SPA、额外缓存框架；优先用现有服务与最小迁移。
- **不伪造验证**：只报告实际执行的命令与结果；没跑就说没跑。

## 4. 环境基线

- 仓库：`C:\Users\World\source\repos\whalepod`
- .NET SDK：10.0.4xx；PostgreSQL 18。
- 构建：`dotnet build WhalePod.slnx -c Release`
- 测试：`WHALEPOD_TEST_DB_CONNECTION=... dotnet test WhalePod.slnx -c Release`
- 集成测试使用真实 PostgreSQL；数据库名需包含 `whalepod_test`（现有守卫）。本轮审查使用 `127.0.0.1:55438`，但新会话应新建 `whalepod_test_fix_<日期>`，不要复用或删除 `whalepod_browser_review`、`whalepod_test_review`。
- 不要使用 `127.0.0.1:55432` 开发库，除非你已确认它是可弃库。
- 详细环境与数据库安全步骤见 [01-baseline-safety.md](01-baseline-safety.md)。

## 5. 证据与交接

每个阶段结束必须输出：

1. 基线：构建/测试/跳过数量；数据库目标（host/port/db）。
2. 变更：`git diff --stat`、涉及文件、迁移。
3. 验证：实际命令与结果；浏览器截图/控制台；并发测试的最终状态。
4. 未完成/阻塞：需要产品决策或外部环境的事项。
5. 交接：下一阶段应读的文件、已知风险、建议 commit message。

维护 `docs/reports/fix-log-2026-10-28.md`（新文件）记录每个 ID 的状态：`fixed / false-positive / blocked / deferred`。**不要直接改写审查报告的历史结论**；审查报告是基线快照。
