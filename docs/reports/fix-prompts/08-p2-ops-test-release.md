# 阶段 7：运维、测试门禁、发布链与 P3 加固

> 先粘贴 `00-master.md`。本阶段覆盖 P2-26～P2-32 与 P3-03～P3-12。部分事项属于产品/基础设施决策，先问再改。

## 范围

### P2

- **P2-26**：`/healthz/ready` 只验证 `CanConnect`，空库/旧 schema 会判就绪。
- **P2-27**：迁移 Down 在已有新枚举/新字段数据时失败。
- **P2-28**：三个空占位测试；E2E 无浏览器自动化；Release 无跳过门禁；HTTP factory 只跑 Development。
- **P2-29**：无依赖锁文件；SDK `latestFeature`；CI/发布镜像/Action 可变；构建不可复现。
- **P2-30**：负载脚本 20–60 秒、无阈值；不能作为 10 分钟容量门禁。
- **P2-31**：traceability 声称 20 个 UAT“已执行”，acceptance 说 UAT-15 未执行；历史证据缺命令/产物/签署。
- **P2-32**：Release 只产出 ZIP，无镜像/部署/迁移/备份调度产物；Shell 脚本 100644。

### P3（可选，但在本阶段一起处理）

- P3-03：CSP 只有 frame-ancestors，无实质 script-src/default-src；无 Referrer-Policy；AllowedHosts `*`。
- P3-04：生产邮件 PublicBaseUrl 允许 HTTP；SMTP 可显式 `None`。
- P3-05：单实例浏览去重冷缓存并发重复计数。
- P3-06：Sitemap 固定 5,000 条，无 index/分片。
- P3-07：两个巨型文件/重复组合根（架构债）。
- P3-08：Contracts 名不副实（设备协议 vs HTTP DTO）。
- P3-10：站点地图/信息流一次性读全量（性能债）。
- P3-11：axe/浏览器验收资产不在仓库。
- P3-12：Agent 解析失败不计熔断（已在阶段 6 修，如未做则在此补）。

## 必读

- 报告对应章节；`docs/design/production-readiness.md`、`docs/design/acceptance.md`、`docs/design/traceability.md`、`docs/design/performance.md`。
- `.github/workflows/ci.yml`、`release.yml`、`pages.yml`
- `global.json`、`Directory.Build.props`、`.editorconfig`、`.gitattributes`
- `src/WhalePod.Api/Program.cs`、`src/WhalePod.Web/Program.cs`（health/ready）
- `src/WhalePod.Infrastructure/Persistence/Migrations/*`（Down）
- `tests/WhalePod.Tests.EndToEnd/*`、`tests/load/*`
- `scripts/*.sh`、`scripts/*.ps1`
- `src/WhalePod.Infrastructure/Services/ViewCounterService.cs`、`PublicDiscoveryService.cs`

## 实施要求

### A. Ready 与迁移（P2-26/P2-27）

1. ready 检查至少验证：
   - 数据库可连接；
   - 迁移版本与程序期望一致（`__EFMigrationsHistory` 包含最新迁移）；
   - 关键表/扩展存在（或执行一个只读探测查询）。
   不要做重查询；失败返回 503。
2. 迁移 Down：
   - 对已有 `appeal_result` 通知/头像数据的库，Down 不能直接重建旧 CHECK/删字段；
   - 选择：提供数据迁移/归档策略，或明确“只支持备份恢复，不支持数据库降级”；
   - 用含新数据的演练集验证；记录数据损失窗口。
3. 测试：空库/旧迁移/正常库的 ready；Down 演练。

### B. 测试门禁（P2-28）

1. 删除三个 `UnitTest1.cs` 空占位（确认项目仍能运行）。
2. 增加 Production 启动矩阵：
   - 缺 `GitHub:ClientId/ClientSecret`、`Audit:IpHashSecret`、`DataProtection:KeysDirectory` 等 → 启动失败；
   - 配置齐全 + fake OAuth → 启动成功；
   - `Auth:DevLoginEnabled=true` 在 Production → 启动失败。
3. E2E：至少增加一个真实浏览器 smoke（启动 Web + 临时库）：
   - 未激活激活页 CSS/JS 200；
   - 登录/激活/发帖/审核/评论/公开可见；
   - 375px 无 JS。
   如果 Playwright 不在 CI 环境，可先作为本地脚本 + 文档，但不要用空占位冒充 E2E。
4. Release/CI 统一 TRX 跳过门禁：0 skipped；DatabaseFact 必须真跑。
5. 测试计数/文档同步：不要再把空占位算入“通过”。

### C. 可复现构建（P2-29）

1. 固定 SDK：`global.json` 用具体版本 + `rollForward: latestPatch`（或至少不用 `latestFeature`）；CI 用同一版本。
2. 引入依赖锁定：`RestorePackagesWithLockFile=true` + `packages.lock.json`，CI `--locked-mode`（评估 .NET 10 支持）。
3. 固定 Actions/镜像/PostgreSQL 版本（commit SHA 或明确标签）；避免可变 `10.0.x`。
4. 记录发布产物来源/哈希；同一提交可复现。
5. 注意：锁文件会新增大量文件；先说明并询问是否接受。

### D. 负载与容量（P2-30）

1. 负载脚本参数化：
   - `DURATION` 默认 10 分钟（可覆盖）；
   - 正式测试身份注入（不要依赖开发登录）；
   - 阈值：p95/p99、错误率、丢弃迭代；达到 SRS 指标才 PASS。
2. 增加 checks/失败退出码；CI 可选（手动预发执行）。
3. 不要在本地用 10 分钟压测阻塞开发；提供手动命令与报告模板。
4. 与本轮报告一致：未执行 10 分钟压测，不宣称容量通过。

### E. 文档/证据（P2-31）

1. 修正 traceability/acceptance 的口径：
   - “已建立并执行” vs “部分/未执行”；
   - UAT-15 未执行；
   - 历史 axe/构建/恢复结论补充命令、日期、提交、原始产物路径、签署人（没有就标“未留存”）。
2. 配置参考补列 `DeviceVerification`、`AgentRateLimit`、`WebRequestLimits` 等实际运行项。
3. 不要把“本地通过”写成“生产通过”；外部前置单列。

### F. 发布链（P2-32）

1. Release 增加可执行交付物：
   - 容器镜像（Dockerfile/Compose 或腾讯云 TKE 清单）；
   - 迁移发布步骤/脚本；
   - 备份调度、Data Protection/对象存储一致恢复说明；
   - 回滚/切流 runbook。
2. 如果基础设施不在仓库范围，明确“仓库不交付部署产物”，并删除文档中“按镜像部署”的暗示；需要产品/运维确认。
3. `scripts/*.sh` 加可执行位（`git update-index --chmod=+x`），或文档统一用 `bash scripts/...`；两者选一。
4. 在干净目标机演练：安装、迁移、启动、备份、恢复、回滚；记录证据。没有环境就标外部前置。

### G. P3 加固（按优先级）

1. 安全头：增加实质 CSP（先 `Content-Security-Policy-Report-Only`）、`Referrer-Policy`、Host 白名单；不要破坏 Blazor/内联脚本；逐步收紧。
2. 邮件：Production 拒绝 `PublicBaseUrl` 非 HTTPS、SMTP `None`/机会式降级；开发例外显式。
3. 浏览去重：原子占位/持久唯一记录；测同键并发。
4. Sitemap：index + 分片；测 >5000。
5. 架构债：先出拆分计划，不要在本阶段大重构；优先把 `Program.cs` 端点按领域拆文件、Agent 服务按用例拆类。
6. Contracts：明确边界；若要迁 DTO，单独评估，不要顺手大迁移。
7. axe/浏览器脚本入库；至少保存命令/报告。
8. 性能：公开侧栏/feed 全量读取，先测量再改；不要无证据优化。

## 验收

- Ready 能识别空库/旧迁移；Down 有明确策略与演练；测试门禁 0 skipped；Production 启动矩阵可运行。
- 锁文件/SDK/镜像可复现（或明确不采纳并记录原因）。
- 负载脚本能跑 10 分钟并按阈值失败。
- 文档不再自相矛盾；发布链有可执行产物或明确外部前置。
- P3 项按优先级完成；未完成的列明原因。

## 停止条件

- 部署/镜像/云资源属于外部运维：先问范围，不要伪造。
- 锁文件/大重构会显著扩大 diff：先问。
- CSP 需要改大量内联脚本：先做 report-only 并给计划，不要一次性强推。
- 迁移 Down 涉及数据丢失：先问保留策略。

## 输出

按 `00-master.md` 的阶段结果格式；更新 `fix-log` 中 P2-26～P2-32、P3 项。建议分多个 commit（测试门禁、构建、文档、发布、加固），但不要自动提交。
