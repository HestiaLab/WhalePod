# 阶段 4：恢复脚本数据安全与 OpenAPI 契约

> 先粘贴 `00-master.md`。本阶段覆盖 P1-07 的恢复脚本部分与 P1-08。测试库守卫应在阶段 0 已完成；本阶段不要重复改守卫。

## 范围

- **P1-07（恢复部分）**：`restore-postgres.sh/.ps1` 使用 `pg_restore --clean --if-exists --exit-on-error`，没有 `--single-transaction`；校验文件缺失仍继续；连接串解析忽略 SSL Mode/证书、分号拆分不安全。
- **P1-08**：OpenAPI 声明 7 个未注册的管理 API 操作（邀请列表/创建/停用、分类列表/创建/更新、审计查询）；按契约调用返回 404。

## 必读

- 报告 P1-07、P1-08。
- `scripts/backup-postgres.sh`、`scripts/restore-postgres.sh`、PowerShell 对应文件
- `docs/design/production-readiness.md` 的备份/恢复章节
- `docs/api/openapi.yaml`：`servers`、`/admin/invites`、`/admin/categories`、`/admin/audit`
- `src/WhalePod.Api/Endpoints/AdminEndpoints.cs`
- `src/WhalePod.Api/Program.cs` 的端点注册
- 现有脚本/契约测试（如有）

## 实施要求

### A. 恢复脚本原子性与连接安全

1. **优先恢复到新库**：
   - `restore` 默认创建/使用一个新库，校验后再由运维切换；
   - 如果保留原位恢复，必须使用 `--single-transaction`（或可证明等价的失败回滚），并在失败时保持原库不变；
   - 不要在没有校验文件时继续恢复；校验失败直接退出。
2. **连接串解析**：
   - 用 libpq 可识别的方式传递 host/port/db/user/password/sslmode 等；不要只用 `IFS=';'` + `xargs`；
   - 支持带引号/分号/空格的密码；不要回显密码；
   - SSL Mode/证书参数必须传递；生产要求 TLS 时不能静默降级；
   - PowerShell 版本保持同等语义。
3. **拒绝系统库/危险目标**：保留现有 `postgres/template0/template1` 拒绝；数据库名白名单；二次确认。
4. **测试**：
   - 用一个可弃库做“恢复中途失败”测试（例如注入无效对象/权限错误），断言原库未被部分清理；
   - 缺校验文件/校验失败：不执行恢复；
   - 带 `SSL Mode=VerifyFull`（或等效）的连接串：dump/restore 参数包含 TLS 设置；无证书时明确失败而不是降级；
   - 密码含分号/引号/空格：解析正确；
   - PowerShell/Shell 至少各验证一次参数解析（能在当前 OS 执行的）。
5. 不要求真实生产环境；用本地可弃库和 fake/无效 dump 即可。

### B. OpenAPI 契约对齐

1. 先做决策（需要用户确认）：
   - **方案 A**：实现 7 个管理 API 操作（推荐，如果 README/契约承诺 API 管理）；
   - **方案 B**：从 OpenAPI 删除这些操作，并在 README/architecture 明确“管理仅 SSR”。
   不要默认选 B 来减少工作量；先问。
2. 若实现：
   - 复用现有 `InviteAdminService`、`CategoryAdminService`、`AuditAdminService`；
   - 端点必须 `RequireAuthorization` + 管理员校验（服务层 `RequireAdminAsync`）+ 写端点 CSRF + 限流；
   - 响应 schema 与 OpenAPI 一致；分页/cursor/错误码一致；
   - 补 HTTP 测试：管理员成功、非管理员 403、未登录 401、CSRF、分页、参数校验。
3. 若删除：
   - 只删契约与文档中明确不提供的操作；保留 SSR 管理页面；
   - 更新 README/architecture/traceability，明确 API 范围；
   - 加一条契约测试，防止未来再漂移。
4. **契约测试**（无论 A/B）：
   - 解析 `openapi.yaml` 的 paths/methods；
   - 从 API 的 `EndpointDataSource` 读取实际注册的 `/api/v1` 路由；
   - 断言契约中的每个 operation 都能命中，或多方明确标注的例外；
   - 该测试应在 CI/本地可运行，不依赖数据库（或使用最小 host）。
5. 不要留下“文档声明但 404”的操作。

## 验收

- 恢复失败不会留下半恢复库；缺校验文件拒绝；SSL 参数不丢失；密码特殊字符正确。
- OpenAPI 与实际路由差异为 0（或明确的、有文档的 SSR-only 例外）。
- 全量构建/测试通过，0 failed、0 skipped。
- 脚本改动的行尾/可执行位：若修改 Shell 脚本，检查 Git 模式；如需要可执行位，说明并询问是否 `git update-index --chmod=+x`（不要擅自改权限）。

## 停止条件

- 需要决定 OpenAPI 实现 vs 删除：先问。
- 需要真实生产库/证书演练：列为外部前置，不要伪造。
- 恢复策略涉及数据丢失窗口：先说明并问。
- 需要修改现有迁移历史：不要；新增迁移或文档说明。

## 输出

按 `00-master.md` 的阶段结果格式；更新 `fix-log` 中 P1-07（恢复部分）、P1-08。建议 commit message 示例：`fix(ops): 加固备份恢复并补齐管理 API 契约`。不要 commit/push。
