# WhalePod v0.1.0 架构与代码质量审查报告

| 项目 | 内容 |
|---|---|
| 审查日期 | 2026-10-28 |
| 审查基线 | `8fd6f6c8af4c7f0a74f1b32b34bc6b00863ca1b6` |
| 审查标准 | 正式交付标准：正确性、架构边界、可维护性、安全、可靠性与可测试性 |
| 审查方式 | 6 个领域静态深审 + 独立隔离动态验证 + 主审跨域复核 |
| 产品基线 | `docs/SRS.md` v0.1.0、`docs/design/architecture.md`、`docs/api/openapi.yaml` |
| 结论 | **不建议按“生产上线条件已满足”签收；存在 8 项 P1 阻断项。工程基础较好，但 Web 交付完整性、生产初始化、状态机与数据生命周期仍有实质缺口。** |

> 本报告只记录本轮实际读取、执行或复现的证据。文档中已有的“本地验收通过”不自动视为本轮通过；未执行的生产环境、外部服务与物理设备验证统一列在“限制与未验证项”。

---

## 1. 结论摘要

### 1.1 总体判断

WhalePod 的代码组织明显高于一般早期项目：项目依赖方向清晰，领域层无反向依赖；公开内容过滤、Markdown 清洗、审计只追加、CSRF、Cookie 安全属性、上传重编码、真实 PostgreSQL 集成测试等都有实质实现，不是只写在文档里。Release 构建 0 警告、0 错误，格式检查通过，依赖漏洞扫描未发现已知漏洞。

但按正式交付标准，当前版本不能直接签收：

1. **新邀请用户的激活页静态资源被激活门禁拦截**，页面无样式、Blazor 模块脚本加载失败；这是每个新用户都会经过的入口。
2. **Web 端缺少大量 SRS 明确要求的核心操作**：作者编辑/驳回后重提、点赞、采纳、举报、评论删除、图片上传 UI、最多 5 份产物；这些能力目前主要存在于 JSON API。
3. **生产 GitHub OAuth 登录没有接入 `Auth:BootstrapAdminHandles`**，按当前代码生产环境无法通过配置获得首个管理员。
4. **申诉与审核状态机存在跨动作覆盖**：旧申诉可能撤销后来发生的新处罚或新裁决。
5. **设备撤销通知去重键固定**，同一设备重新验证后第二次撤销会因唯一键冲突回滚。
6. **账号选择“删除内容”注销时，帖子附图仍为 attached 状态**，媒体清理永远不会选中，且公开 URL 仍可读取。
7. **测试库保护与恢复脚本存在数据安全缺口**：测试库只检查连接串是否包含 `whalepod_test` 子串；恢复脚本没有事务级失败回退。
8. **OpenAPI 声明了 7 个未注册的管理 API 操作**，按契约调用会得到 404。

### 1.2 分维度评分

| 维度 | 评分 | 一句结论 |
|---|---|---|
| 架构与分层 | **B** | 模块化单体方向正确，依赖无环；但两个组合根重复注册、契约项目名不副实、两个巨型文件开始成为维护瓶颈。 |
| 安全与隐私 | **C+** | Cookie/CSRF/清洗/审计基础扎实；会话撤销、临时媒体生命周期、CSP、搜索入口保护仍有缺口。 |
| 业务正确性 | **C** | 正常路径可用，但申诉、采纳、活跃排序、敏感词、板块规则存在跨动作或“配置未接线”问题。 |
| 数据一致性与并发 | **C+** | 有版本号、行锁、唯一索引等基础；Agent 租约、设备上限、邮件 outbox 缺少 fencing/原子占位。 |
| Web 完整性与可访问性 | **D+** | SSR 基础和无 JS 退化有设计，但核心互动入口缺失，激活页资源被拦截，失败输入保留不完整。 |
| 测试与验证 | **B-** | 156 项真实数据库测试是亮点；但 E2E 为空占位、关键反例缺失、测试库防误删不足。 |
| 运维与发布 | **C** | 有备份、健康检查、生产前置文档；恢复原子性、发布产物链、可复现构建仍需补齐。 |
| 可维护性 | **B-** | 格式与命名整体规范；`Program.cs` 2,581 行、`AgentModerationService.cs` 1,629 行、测试 1,118 行，已影响审查与修改成本。 |

**综合：C+（约 68/100）。** 分数主要被“功能未交付到 Web”“生产初始化断链”“跨状态覆盖”拉低，而不是被代码风格拉低。

---

## 2. 本轮实际验证结果

### 2.1 构建、测试与静态检查

| 检查 | 命令/方式 | 结果 |
|---|---|---|
| Release 构建 | `dotnet build WhalePod.slnx -c Release --no-restore` | **PASS**：10 个项目，0 警告，0 错误，13.21 s |
| 单元测试 | `dotnet test ... -c Release --no-build` | **PASS**：23/23 |
| 集成测试 | 独立 PostgreSQL 18，`WHALEPOD_TEST_DB_CONNECTION` 指向 `whalepod_test_review` | **PASS**：158/158，24 s，0 跳过 |
| E2E 测试 | 同上 | **PASS 但无意义**：1 项为空占位 |
| 格式 | `dotnet format WhalePod.slnx --verify-no-changes --no-restore` | **PASS**：退出码 0，无输出 |
| 依赖漏洞 | `dotnet list WhalePod.slnx package --vulnerable --include-transitive` | **PASS**：当前源未报告易受攻击包 |
| 数据库迁移 | `dotnet ef database update` 到全新库 | **PASS**：5 个迁移全部应用 |
| 备份/恢复 | 备份隔离库 → 恢复到新库 | **PASS**：帖子与 6 条审计记录一致；但恢复脚本原子性有问题，见 P1-07 |

> 首次测试运行使用数据库名 `whalepod_review`，156 项 `DatabaseFact` 被测试保护跳过（测试仍以退出码 0 结束）。随后改用符合保护规则的 `whalepod_test_review` 重新运行，才得到 158/158。这个过程本身也说明“退出码 0”不等于“数据库测试真的跑了”。

### 2.2 浏览器与 HTTP 动态验证

环境：隔离数据库 `127.0.0.1:55438/whalepod_browser_review`；Web `127.0.0.1:55224`；API `127.0.0.1:55010`；仅 Development、Logging 邮件、本地媒体目录；未触碰 `55432` 开发库或任何外部服务。

| 场景 | 结果 |
|---|---|
| 新建板块 → 编辑器发帖 → 待审 | **PASS**：帖子 `moderation_status=pending`，匿名访问 404 |
| 管理台快速通过 → 评论 → 审核通过 → 匿名可见 | **PASS**：帖子/评论均为 `approved`，版本递增；数据库与页面一致 |
| 375×812、关闭 JavaScript 的公开帖详情 | **PASS**：正文与已审核评论可见，`scrollWidth=375`，无横向溢出 |
| 1280px 桌面帖详情 | **PASS**：布局正常，无观察到重叠 |
| 搜索、RSS、Sitemap、robots | **PASS**：均返回 200，已公开帖子可被发现 |
| 未激活用户访问激活页 | **FAIL**：指纹化 CSS 与 `ReconnectModal.razor.js` 被 302 到 `/activate`，浏览器报 MIME 错误，页面无样式 |
| 退出登录后使用旧 Cookie 访问 API | **FAIL**：`POST /logout` 返回 302 后，旧 `whalepod_session` 仍可 `GET /api/v1/auth/csrf` 得到 200 |
| 临时图片上传后匿名读取 | **FAIL**：上传返回 201；未挂帖的 `/media/...webp` 匿名 200，`Cache-Control: public, max-age=31536000, immutable` |
| API 匿名搜索 35 次 | **FAIL**：35 次全部 200；同一时刻 Web `/search` 为 30×200 + 5×429 |
| 非法游标（负数 ticks） | **FAIL**：HTTP 500 |
| RSS 撤回验证 | **FAIL**：隐藏非最大更新时间帖子后，带旧 ETag 的请求仍返回 304 |
| 发帖校验失败 | **FAIL（有部分补偿）**：服务端重定向后标题/正文/内核/图片 ID 全空；仅 JS 本地草稿可手动恢复，评论无草稿 |
| 评论校验失败 | **FAIL**：输入 1 个字符后提交，重定向到 `?error=invalid`，文本框为空 |

截图与原始日志：`artifacts/review-20261028/`（`post-desktop.png`、`post-mobile-no-js.png`、`pending-activation.png`、`build.log`、`integration-tests.log`、`browser-evidence.json` 等）。

### 2.3 未执行或未证实的部分

- 真实 GitHub OAuth 回调、SMTP、COS/S3、CDN/WAF、腾讯云预发迁移与回滚。
- 真实屏幕阅读器、物理 iOS/Android/iPad、macOS/Linux CLI 实机与签名/公证。
- 10 分钟容量压测；本轮只做了小规模限流对比，不能据此判断容量。
- 多实例并发下的邮件 outbox、设备上限、Agent 租约竞争；部分已由隔离复现任务覆盖，未覆盖部分在文中标为静态确认。
- 生产环境 CSP、HSTS、代理头与 Data Protection 共享目录的现场配置。

---

## 3. 架构评估

### 3.1 值得肯定

1. **依赖方向清晰**：`Domain`、`Contracts` 无项目引用；`Infrastructure` 只依赖 `Contracts/Domain`；Web/API 依赖业务核心；20 条 ProjectReference 无环。
2. **双宿主边界有明确意图**：Web 只做 SSR/表单/管理页面，API 承载 JSON、Agent 与后台 worker；worker 只注册在 API，避免 Web 重复发送邮件/清理媒体。
3. **安全控制不是装饰**：Cookie `HttpOnly/Secure/SameSite=Lax`、CSRF 双提交、Data Protection 共享密钥、请求体上限、写请求限流、上传内容嗅探与重编码、Markdown 禁原始 HTML/白名单清洗、审计触发器只追加，均有代码与测试支撑。
4. **公开可见性有统一出口**：公开查询显式过滤 `approved + deleted_at is null`，作者/管理员可见性单独处理；没有依赖容易漏配的全局过滤器。
5. **数据层有真实约束**：循环外键、部分索引、采纳唯一索引、点赞复合主键、通知/邮件去重索引、审计 UPDATE/DELETE 拒绝触发器都落在迁移里。
6. **文档与契约意识强**：SRS、ERD、OpenAPI、验收、生产前置、性能文档齐全；OpenAPI 的 484 处引用可解析。

### 3.2 主要架构风险

1. **契约项目名不副实**：`WhalePod.Contracts` 实际只有设备协议（1 个文件、300 行），大部分 HTTP DTO/错误码在 `Infrastructure`，Agent 还使用手写 JSON。跨项目契约没有单一事实来源，导致 OpenAPI 与实现漂移（P1-08）。
2. **两个组合根重复注册 16 种服务**，Cookie、CSRF、Data Protection、代理、限流配置各自维护；搜索限流只在 Web 实现、API 漏掉，就是这种重复维护的直接后果。
3. **两个巨型文件**：`src/WhalePod.Web/Program.cs` 2,581 行、`src/WhalePod.Infrastructure/Services/AgentModerationService.cs` 1,629 行。端点、策略、序列化、状态机混在一起，审查成本和回归风险都高。
4. **“配置存在”与“行为接线”之间缺少契约测试**：敏感词策略、BootstrapAdminHandles、OpenAPI 管理端点都是“有配置/有文档/有服务”，但没有端到端消费者；这类缺口最适合用契约/启动矩阵测试兜底。
5. **并发模型不统一**：内容使用 `Version` 乐观并发；设备上限、Agent 租约、邮件 outbox 使用“先读后写 + 条件更新”，但没有 fencing token 或行锁。默认单实例时可接受，多实例/重试时会暴露问题。

---

## 4. 问题总览

| ID | 级别 | 问题 | 验证状态 |
|---|---|---|---|
| P1-01 | P1 | 未激活用户激活页的指纹化 CSS/JS 被门禁 302，页面无样式、模块脚本失败 | ✅ 动态复现 |
| P1-02 | P1 | Web 缺少编辑/重提、点赞、采纳、举报、评论删除、图片上传 UI、多产物等核心闭环 | ✅ 静态确认 + 浏览器观察 |
| P1-03 | P1 | 生产 GitHub OAuth 未接入 `BootstrapAdminHandles`，生产无法按配置产生首个管理员 | ✅ 静态确认 |
| P1-04 | P1 | 旧申诉可撤销后来发生的新处罚/新内容裁决 | ✅ 静态确认（隔离复现中） |
| P1-05 | P1 | 设备重新验证后再次撤销，因固定通知去重键触发唯一约束，撤销回滚 | ✅ 静态确认（隔离复现中） |
| P1-06 | P1 | 注销选择“删除内容”时帖子附图仍为 attached，清理器永不选中，公开 URL 继续可读 | ✅ 静态确认（隔离复现中） |
| P1-07 | P1 | 测试库保护只做子串检查；恢复脚本无 `--single-transaction`，失败可留下半恢复库 | ✅ 静态确认 |
| P1-08 | P1 | OpenAPI 声明 7 个未注册的管理 API 操作（邀请/分类/审计） | ✅ 动态 404 + 静态对照 |
| P2-01 | P2 | 退出登录只清客户端 Cookie，旧会话在 API 仍有效 | ✅ 动态复现 |
| P2-02 | P2 | API 搜索无限流/超时，可绕过 Web 的 30 次/分钟保护 | ✅ 动态复现 |
| P2-03 | P2 | 负数/超范围游标触发未捕获异常，公开参数可造成 500 | ✅ 动态复现 |
| P2-04 | P2 | 提交失败不保留服务端输入；评论无草稿，发帖仅 JS 本地草稿可手动恢复 | ✅ 动态复现 |
| P2-05 | P2 | 评论分页改变回复归属；深度 >6 的回复被静默截断 | ✅ 静态确认 |
| P2-06 | P2 | 危险后台操作依赖 JS `confirm`，无 JS 时直接执行；“快速通过”无确认 | ✅ 静态确认 |
| P2-07 | P2 | 已采纳评论编辑进入 pending / 被驳回后，父帖仍 solved 且指向不可见答案 | ✅ 静态确认（隔离复现中） |
| P2-08 | P2 | 申诉复核说明允许 1,000 字，但写入 `varchar(500)` 的 ModerationAction.Reason | ✅ 静态确认（隔离复现中） |
| P2-09 | P2 | 申诉目标删除后无法结案；队列固定取最旧 50 条，可被失效申诉堵塞 | ✅ 静态确认 |
| P2-10 | P2 | 敏感词“命中即拒绝”只存配置，无写入/审核消费者 | ✅ 静态确认 |
| P2-11 | P2 | `LastActivityAt` 只在发帖时写入，评论/采纳不会更新“最近活跃” | ✅ 静态确认 |
| P2-12 | P2 | 板块显示名硬编码决定“抄作业”必须填内核，改名即改变业务规则 | ✅ 静态确认 |
| P2-13 | P2 | 两个不同 challenge 并发完成可突破活跃设备上限 | ✅ 静态确认 |
| P2-14 | P2 | Agent 每日预算无原子占位；内容额度会阻断已领取任务 | ✅ 静态确认（隔离复现中） |
| P2-15 | P2 | Agent 释放租约使用旧实体全列保存，可清除别人刚领取的新租约 | ✅ 静态确认（隔离复现中） |
| P2-16 | P2 | MCP 请求超时/取消会结束整个会话，进程仍返回 0 | ✅ 静态确认 |
| P2-17 | P2 | Agent 允许向任意 HTTP 地址发送 Bearer 服务令牌 | ✅ 静态确认 |
| P2-18 | P2 | Agent 任务内容脱敏漏掉产物名称 `artifact.Name` | ✅ 静态确认 |
| P2-19 | P2 | Agent 任务游标截断到毫秒，同优先级任务可分页重复 | ✅ 静态确认（隔离复现中） |
| P2-20 | P2 | 非法 JSON/未知字段的建议提交不进入失败熔断计数 | ✅ 静态确认 |
| P2-21 | P2 | Device CLI 的 `uname` 超时检查在同步 `ReadToEnd` 之后，挂起时永不生效 | ✅ 静态确认 |
| P2-22 | P2 | 邮件 outbox 租约无 fencing，过期持有者可把已发送状态改回 pending/failed | ✅ 静态确认（隔离复现中） |
| P2-23 | P2 | 媒体清理固定取最早 N 条，失败对象可饿死后续清理 | ✅ 静态确认 |
| P2-24 | P2 | 备份状态不校验新鲜度；健康检查把其他异常误报为数据库故障 | ✅ 静态确认 |
| P2-25 | P2 | 评论审核通知/邮件缺少目标链接 | ✅ 静态确认 |
| P2-26 | P2 | `/healthz/ready` 只验证 `CanConnect`，空库/旧 schema 会被判就绪 | ✅ 静态确认 |
| P2-27 | P2 | 迁移 Down 在已有新枚举/新字段数据时会失败 | ✅ 静态确认 |
| P2-28 | P2 | 三个测试项目各有一个空占位；E2E 无浏览器自动化；Release 无跳过门禁 | ✅ 静态确认 + 测试结果 |
| P2-29 | P2 | 无依赖锁文件，SDK 使用 `latestFeature`；发布矩阵重新 restore，不保证可复现 | ✅ 静态确认 |
| P2-30 | P2 | 负载脚本最长 60 秒、无通过阈值，不能作为 10 分钟容量门禁 | ✅ 静态确认 |
| P2-31 | P2 | traceability 声称 20 个 UAT 已执行，acceptance 明说 UAT-15 未执行 | ✅ 静态确认 |
| P2-32 | P2 | Release 只产出 ZIP，无镜像/部署/迁移/备份调度产物；Shell 脚本模式 100644 | ✅ 静态确认 |
| P3-01 | P3 | 正常页面没有退出登录入口，只能直接访问 `/activate` | ✅ 浏览器观察 |
| P3-02 | P3 | 封禁后旧 Cookie 仍可读取自己的 pending/rejected 详情（仅自己的内容） | ✅ 静态确认 |
| P3-03 | P3 | 安全响应头不完整：仅有 `frame-ancestors` 级 CSP，无 `Referrer-Policy`；`AllowedHosts="*"` | ✅ 动态响应头确认 |
| P3-04 | P3 | 生产邮件根 URL 允许 HTTP，SMTP 可显式配置 `None` | ✅ 静态确认 |
| P3-05 | P3 | 单实例浏览去重也存在冷缓存并发重复计数 | ✅ 静态确认 |
| P3-06 | P3 | Sitemap 固定 5,000 条上限，无 index/分片 | ✅ 静态确认 |
| P3-07 | P3 | 两个组合根重复注册与巨型文件 | ✅ 统计确认 |

> 说明：P1 为正式交付/生产启用阻断；P2 为发布前应修复或明确豁免；P3 为加固或已知限制。未发现 P0（无需凭据的认证绕过、任意代码执行、SQL 注入或已证实的跨用户数据泄露）。

---

## 5. P1 阻断项详述

### P1-01 未激活用户激活页的静态资源被门禁拦截

**证据**

- [ActivationGateMiddleware.cs](../../src/WhalePod.Web/ActivationGateMiddleware.cs#L14-L39) 的允许前缀是 `/app.css`、`/WhalePod.Web.styles.css`、`/_framework`、`/_blazor` 等；未包含指纹化文件名与 `Components/**/*.razor.js`。
- [App.razor](../../src/WhalePod.Web/Components/App.razor#L23-L24) 通过 `@Assets["app.css"]` 与 `@Assets["WhalePod.Web.styles.css"]` 输出带哈希的路径；[ReconnectModal.razor](../../src/WhalePod.Web/Components/Layout/ReconnectModal.razor#L1) 输出 `Components/Layout/ReconnectModal.<hash>.razor.js`。
- 动态验证：未激活账号访问 `/activate` 时，`/app.8xc34ax7np.css`、`/WhalePod.Web.dup9j8glep.styles.css`、`/Components/Layout/ReconnectModal.abdmv1u4y3.razor.js` 均返回 302 到 `/activate`，浏览器报 `Expected a JavaScript-or-Wasm module script but the server responded with MIME type text/html`；截图见 `artifacts/review-20261028/pending-activation.png`。

**影响**

这是新邀请用户必经的激活入口：页面无样式（截图显示鲸鱼图标巨大、排版完全失效），Blazor 重连模块加载失败。功能表单仍可提交，但首屏体验与“正式交付”不符，且会影响后续交互增强。

**修复建议**

- 不要用硬编码前缀判断静态资源；在激活门禁中优先放行静态资源端点（`MapStaticAssets` 生成的 endpoint metadata），或仅对动态页面/写请求做激活检查。
- 若保留允许列表，至少覆盖指纹化 CSS、scoped CSS、`_framework`、`_blazor`、`Components/**/*.razor.js`、`media`、`favicon`，并对未知静态扩展名做兜底。
- 增加“未激活用户”端到端测试：断言 CSS/JS 返回 200 且内容类型正确，并截图/检查关键布局。

**验收标准**

未激活用户访问 `/activate` 时，页面所有 CSS/JS 均 200；无控制台 MIME/404 错误；激活表单在 375px 与 1280px 下可正常使用；激活后跳转不受影响。

---

### P1-02 Web 端缺少核心互动与编辑闭环

**证据**

- [Editor.razor](../../src/WhalePod.Web/Components/Pages/Editor.razor#L45) 只有一个 `action="/editor/create"` 表单；页面列表中没有编辑页（`Components/Pages` 下无 `Edit` 路由）。
- [Editor.razor:133-143](../../src/WhalePod.Web/Components/Pages/Editor.razor#L133-L143) 要求用户“先通过 `POST /api/v1/uploads/images` 上传，再手工粘贴上传 ID”；没有上传进度、失败重试、替代文本输入。
- [Editor.razor:146-172](../../src/WhalePod.Web/Components/Pages/Editor.razor#L146-L172) 明确写“产物（可选，最多 1 份）”，而 SRS F-038 AC1 要求最多 5 份，AC8 要求可删除、调整顺序。
- [Editor.razor:224](../../src/WhalePod.Web/Components/Pages/Editor.razor#L224) 只取标签第一页 50 项，忽略 `nextCursor`。
- [PostDetail.razor:216-234](../../src/WhalePod.Web/Components/Pages/PostDetail.razor#L216-L234) 只显示点赞/浏览/复制计数；[PostDetail.razor:286-310](../../src/WhalePod.Web/Components/Pages/PostDetail.razor#L286-L310) 只有评论表单；[PostDetail.razor:446-451](../../src/WhalePod.Web/Components/Pages/PostDetail.razor#L446-L451) 只有“回复”链接，没有点赞、采纳、举报、删除按钮。
- 全仓 `grep` 未在 `src/WhalePod.Web` 找到 `/like`、`/accept`、`/reports` 的前端调用；`wwwroot` 也无对应交互脚本。
- 动态验证：`GET /posts/{id}/edit` 与 `GET /editor/{id}` 均为 404；公开详情页表单只有评论提交与回复链接。
- 对照 SRS：F-033（作者编辑）、F-036 AC4/AC6（上传进度/插入正文）、F-038 AC1/AC8（5 份产物/排序删除）、F-041（驳回反馈与重提）、F-050（点赞）、F-053（采纳）、F-055（举报）、F-054（删除评论确认）。
- 交付声明与代码不一致：[traceability.md:32](../../docs/design/traceability.md#L32) 明确写“评论/点赞/采纳/举报 API 与 SSR……其余覆盖”，但 Web 端没有这些 SSR 操作；[:31](../../docs/design/traceability.md#L31) 则承认“上传 UI 仍需粘贴 MediaUpload ID”，即上传 UI 是已披露的部分缺口。
- 因此本项中 **编辑/重提、点赞、采纳、举报、评论删除属于“声明覆盖但未实现”**；**上传 UI、5 份产物、标签分页属于“已承认部分但正式交付仍需补齐”**。两者都不应被 README 的能力概览掩盖。

**影响**

- 普通成员无法只通过 Web 完成“被驳回 → 修改 → 重提”的闭环，也无法点赞、采纳答案、举报内容或删除自己的评论。
- 编辑器把图片上传降级为“手工调 API + 粘贴 UUID”，不符合 SRS 的成员可用性要求；单份产物直接不满足上限。
- README 的“能力概览”容易被理解为 Web 已交付，实际只有 API 具备这些能力；这是正式交付中最明显的功能缺口。

**修复建议**

- 增加 `/posts/{id}/edit` SSR 页面：回填现有内容、携带 `If-Match` 版本、处理 409 冲突；被驳回内容展示理由与时间。
- 在帖子/评论详情使用原生表单（无需 JS）实现点赞/取消、采纳/取消、举报、删除；服务端继续做权限与状态校验。
- 编辑器接入真实图片上传组件（进度、失败重试、alt 文本），并支持 5 份产物的增删/排序。
- 标签选择器改为可搜索/分页，至少保证第 51 个及更早标签可选。

**验收标准**

- 普通成员仅用浏览器完成：发帖 → 被驳回 → 查看理由 → 编辑 → 重提 → 通过；点赞/取消、采纳/取消、举报、删除评论；无 JS 时基础路径仍可用。
- 图片上传全程不需要手工调用 API；5 份产物可增删排序；所有有效标签均可选且总数 ≤6。
- 越权、版本冲突、禁言状态仍由服务端拒绝，并有 HTTP/服务测试覆盖。

---

### P1-03 生产 GitHub OAuth 未接入管理员引导

**证据**

- [AdminBootstrapService.cs:19-54](../../src/WhalePod.Infrastructure/Services/AdminBootstrapService.cs#L19-L54) 实现“按配置 handle 一次性提升管理员并写审计”。
- 全仓唯一调用点是开发登录：[Program.cs:435-476](../../src/WhalePod.Web/Program.cs#L435-L476)，且该端点只在 Development 且 `Auth:DevLoginEnabled=true` 时可用。
- GitHub OAuth 入口 [GitHubOAuthEndpoints.cs](../../src/WhalePod.Web/GitHubOAuthEndpoints.cs) 与 [GitHubOAuthService.cs](../../src/WhalePod.Web/Services/GitHubOAuthService.cs) 中没有 `EnsureAdminAsync`/`BootstrapAdminHandles` 调用。
- README 与 architecture 都写明 `Auth:BootstrapAdminHandles` 会在登录时提升为管理员，生产通过秘密存储注入。

**影响**

按仓库代码部署生产后，即使正确配置 `Auth:BootstrapAdminHandles`，首个管理员也不会产生；部署者只能手工改数据库，绕过审计与一次性提升逻辑。获取真实 OAuth 凭据是外部前置，但“OAuth 登录后不调用引导服务”是代码缺陷，不应归入外部凭据问题。

**修复建议**

- 在 OAuth 回调成功创建/更新用户后，调用 `EnsureAdminAsync`（同一事务或明确的补偿路径），只对配置中的 handle 生效，并记录审计。
- 明确首管理员激活流程：首次登录是否要求邀请码、是否允许未激活管理员、如何避免重复提升。
- 增加“生产配置 + stub OAuth”测试：指定账号提升、非指定账号不提升、重复登录不重复授权、审计存在。

**验收标准**

生产环境配置 `Auth:BootstrapAdminHandles=["alice"]`，alice 首次 GitHub 登录后角色为 admin 且审计记录存在；其他用户不受影响；重复登录不重复写提升审计。

---

### P1-04 旧申诉可撤销后来发生的新处罚/新裁决

**证据**

- [AppealService.cs:146-155](../../src/WhalePod.Infrastructure/Services/AppealService.cs#L146-L155) 在提交时保存 `RelatedActionId`。
- 账号申诉接受逻辑 [AppealService.cs:382-410](../../src/WhalePod.Infrastructure/Services/AppealService.cs#L382-L410) 只检查当前状态是 `Muted/Banned`，随后直接置为 `Active`，没有校验当前处罚是否仍是申诉对应的那次处罚。
- 内容申诉 [AppealService.cs:302-306](../../src/WhalePod.Infrastructure/Services/AppealService.cs#L302-L306) / [AppealService.cs:351-356](../../src/WhalePod.Infrastructure/Services/AppealService.cs#L351-L356) 只检查当前为 `Rejected`，没有校验版本/裁决 ID。
- [AdminUserService.cs:399-407](../../src/WhalePod.Infrastructure/Services/AdminUserService.cs#L399-L407) 允许 `Muted → Banned`；[PostWriteService.cs:263-273](../../src/WhalePod.Infrastructure/Services/PostWriteService.cs#L263-L273) 允许驳回内容编辑后重新进入 `pending`。

**触发序列**

1. 管理员禁言 A（处罚 1）。
2. A 提交账号申诉（关联处罚 1）。
3. 管理员因另一违规将 A 封禁（处罚 2）。
4. 管理员通过旧申诉 → 代码只看到“当前是 Banned”，直接把 A 恢复为 Active，处罚 2 被一并撤销。

内容路径同理：驳回 A → 申诉 → 作者编辑 → 再次驳回 → 通过旧申诉 → 新版内容被恢复。

**影响**

复核动作针对的历史裁决与实际撤销对象不一致，管理员可能在不知情的情况下解除新处罚或恢复新版本内容；审计记录也无法表达“旧申诉失效”。

**修复建议**

- 申诉绑定具体的处罚/裁决标识（`RelatedActionId`）与内容版本；复核时若当前处罚/裁决不匹配，返回冲突并标记申诉失效或要求重新提交。
- 账号处罚使用独立的 `PenaltyId` 或“处罚代次”，而不是只看 `Status`。
- 内容申诉保存提交时的 `Version`/`ModerationActionId`，通过时做条件更新。
- 增加顺序与并发测试：旧申诉 vs 新处罚、旧申诉 vs 新驳回、复核与新处罚并发。

**验收标准**

上述两个序列执行后，新处罚/新裁决保持有效；旧申诉被标记为失效或拒绝，并写明确审计；并发复核不会覆盖新状态。

---

### P1-05 设备重新验证后再次撤销会因通知去重键冲突回滚

**证据**

- [DeviceVerificationService.cs:446-452](../../src/WhalePod.Infrastructure/Services/DeviceVerificationService.cs#L446-L452) 在同一设备记录上重新激活（复用 `Device.Id`）。
- [DeviceVerificationService.cs:714-727](../../src/WhalePod.Infrastructure/Services/DeviceVerificationService.cs#L714-L727) 撤销通知固定使用 `device-revoked:{Device.Id:N}`。
- 通知唯一索引为 `(user_id, dedupe_key)`：[WhalePodDbContext.Interactions.cs:75](../../src/WhalePod.Infrastructure/Persistence/WhalePodDbContext.Interactions.cs#L75)；邮件 outbox 唯一索引为 `(to_address, dedupe_key)`：[WhalePodDbContext.Communications.cs:31](../../src/WhalePod.Infrastructure/Persistence/WhalePodDbContext.Communications.cs#L31)。
- 撤销流程在事务内先写通知/邮件，再 `SaveChanges`/`Commit`：[DeviceVerificationService.cs:690-729](../../src/WhalePod.Infrastructure/Services/DeviceVerificationService.cs#L690-L729)。

**触发序列**

验证设备 → 撤销（写 `device-revoked:{id}`）→ 用同一密钥重新验证（设备重新 Active）→ 再次撤销 → 通知/邮件唯一键冲突 → 整个事务回滚，设备仍为 Active。

**影响**

用户和管理员都无法再次撤销该设备；安全事件响应被阻断。现有测试只覆盖“同 challenge 并发完成一次”，未覆盖“重新激活后再撤销”。

**修复建议**

- 去重键加入激活周期/撤销代次（例如 `device-revoked:{id}:{activatedAtTicks}` 或 `revokedCount`），或对通知做 upsert。
- 撤销使用事务内行锁/条件更新，确保重复撤销幂等且不因通知侧冲突失败。
- 增加“验证→撤销→重新验证→撤销”和并发撤销测试。

**验收标准**

同一设备连续两轮激活/撤销均成功；每轮最多一条站内通知/一封邮件；第二轮撤销后设备状态为 `revoked`，审计完整。

---

### P1-06 注销“删除内容”不清理帖子附图，媒体仍公开可读

**证据**

- [AccountDeletionService.cs:103-147](../../src/WhalePod.Infrastructure/Services/AccountDeletionService.cs#L103-L147) 只把帖子/评论置为 `Deleted` 并软删除；没有处理 `PostImage` 或 `MediaUpload.Status`。
- [AccountDeletionService.cs:168-176](../../src/WhalePod.Infrastructure/Services/AccountDeletionService.cs#L168-L176) 只把 `Avatar` 状态的上传标记删除。
- [MediaCleanupService.cs:24-45](../../src/WhalePod.Infrastructure/Services/MediaCleanupService.cs#L24-L45) 只选择 `temp/deleted/expired` 等状态，`attached` 永远不会入选。
- [Program.cs:912-933](../../src/WhalePod.Api/Program.cs#L912-L933) 本地媒体静态出口不校验登录/归属/审核状态，并设置一年 `immutable` 缓存。

**影响**

用户选择“删除内容”注销后，帖子正文不可见，但帖子中的图片对象仍存在且可通过原 URL 公开读取；清理器永远不会删除这些对象。这与“删除内容”的用户预期、SRS F-009 AC3 以及数据保留策略不一致。即使图片 URL 不可枚举，它已经被作者/读者获取，不能视为已撤回。

**修复建议**

- 注销删除内容时，将关联 `PostImage`/`MediaUpload` 标记为 `deleted` 并加入对象删除队列；对 `hidden/rejected/deleted` 内容执行同样的媒体生命周期处理。
- 对 `attached` 但所属帖子已删除/隐藏的媒体建立定时审计任务，避免永久残留。
- 明确“保留图片”的可审计策略；若保留，必须有保留期限与访问控制，而不是永久 attached + 公开 URL。

**验收标准**

上传图片 → 发帖 → 注销并选择删除内容 → 清理任务执行后：帖子不可见、图片 URL 返回 404/403、对象存储无残留、审计记录包含媒体清理结果；重复清理幂等。

---

### P1-07 测试库保护与恢复脚本存在数据安全缺口

**证据**

- [PersistenceDatabaseTests.cs:13-27](../../tests/WhalePod.Tests.Integration/PersistenceDatabaseTests.cs#L13-L27) 只用 `connectionString.Contains("whalepod_test")` 判断；连接串的密码、Application Name、Host 等任意位置包含该子串都能通过。
- [MediaCleanupServiceTests.cs:123-126](../../tests/WhalePod.Tests.Integration/MediaCleanupServiceTests.cs#L123-L126) 使用 `ExecuteDeleteAsync()` 无条件删除 `PostImages`、`MediaUploads`；其他测试也有类似清表。
- [restore-postgres.sh:112-117](../../scripts/restore-postgres.sh#L112-L117) 在目标库不存在时创建，然后执行 `pg_restore --clean --if-exists --no-owner --no-privileges --exit-on-error`；没有 `--single-transaction`。
- [restore-postgres.sh:97-110](../../scripts/restore-postgres.sh#L97-L110) 校验文件不存在时继续恢复；[backup-postgres.sh:20-34](../../scripts/backup-postgres.sh#L20-L34) 与 restore 的连接串解析只提取 host/port/database/username/password，忽略 SSL Mode、证书等参数。

**影响**

- 误把生产库连接串写入测试环境（例如密码里含 `whalepod_test`）时，测试会通过保护并执行整表删除。
- 恢复过程中若权限/扩展/数据错误，`--clean` 已删除的对象不会回滚，留下半恢复数据库；校验文件缺失也不阻断。
- 生产连接串要求的 TLS 参数不会被传给 `pg_dump/pg_restore`，可能以更低安全级别连接。

**修复建议**

- 用 Npgsql 连接串解析器校验实际 `Database` 名称、Host 白名单（仅回环/测试主机）和测试专用角色；连接后再执行 `SELECT current_database()` 二次确认。
- 测试 fixture 使用唯一数据库名（如 `whalepod_test_<runid>`）并在结束时删除；禁止对共享库清表。
- 恢复优先到新库，校验后再切换；若原位恢复，使用 `--single-transaction` 或先做可回滚快照；缺少校验文件默认拒绝。
- 传递 libpq 的 SSL 参数或改用 `PGPASSFILE`/服务配置；对带引号/分号的密码使用解析器而不是 `IFS=';'` + `xargs`。

**验收标准**

连接串仅在密码中包含 `whalepod_test` 时，测试在发出任何写 SQL 前拒绝；恢复中途失败后数据库仍保持恢复前状态；带 `SSL Mode=VerifyFull` 的连接串在 dump/restore 中保持 TLS 校验。

---

### P1-08 OpenAPI 声明了未注册的管理 API 操作

**证据**

- OpenAPI `servers` 前缀为 `/api/v1`：[openapi.yaml:38-42](../../docs/api/openapi.yaml#L38-L42)。
- 契约声明了 `/admin/invites`（GET/POST）、`/admin/invites/{id}/disable`、`/admin/categories`（GET/POST）、`/admin/categories/{id}`、`/admin/audit`：[openapi.yaml:4361-4618](../../docs/api/openapi.yaml#L4361-L4618)。
- [AdminEndpoints.cs:9-199](../../src/WhalePod.Api/Endpoints/AdminEndpoints.cs#L9-L199) 只注册了 moderation、reports、users 相关端点。
- 动态验证：`GET /api/v1/admin/categories`、`/api/v1/admin/invites`、`/api/v1/admin/audit` 均返回 **404**；`/api/v1/admin/users` 返回 200。

**影响**

按 OpenAPI 生成的客户端/第三方集成会在这些管理操作上直接失败；README 和架构文档把 API 描述为包含管理 API，契约与实际实现不一致。SSR 管理页面不能替代 JSON 契约。

**修复建议**

- 逐项裁决：要么在 `AdminEndpoints` 实现邀请、分类、审计的 JSON API，要么从 OpenAPI 删除这些操作并明确“管理仅 SSR”。
- 增加契约测试：从 OpenAPI 解析全部路径/方法，与实际 `EndpointDataSource` 对照，差异必须为零。
- 同步更新 README/architecture 的能力描述。

**验收标准**

OpenAPI 中每个 operation 都能在 API 中命中（或明确标注为 SSR-only 且不在 OpenAPI）；契约测试在 CI 中阻断新增漂移。

---

## 6. P2 重要缺陷

### 6.1 会话与匿名入口

**P2-01 退出登录不撤销服务器端会话**

- [Program.cs:521-534](../../src/WhalePod.Web/Program.cs#L521-L534) 只调用 `SignOutAsync` 清除浏览器 Cookie；认证配置为 8 小时滑动 Cookie：[Program.cs:216-235](../../src/WhalePod.Web/Program.cs#L216-L235)。
- 动态验证：登录 → 保存 `whalepod_session` → 通过 `/activate` 表单退出（302）→ 用旧 Cookie 请求 API `/api/v1/auth/csrf` 仍返回 **200**。
- 影响：已复制/被窃取的 Cookie 在退出后仍可继续使用，直到 8 小时滑动过期；不符合 SRS REQ-F-004 AC2/AC3 与 REQ-NF-005 的“注销失效”。
- 建议：引入服务端会话 ID/撤销版本（用户级 `SessionVersion` 或会话表），Web/API 均校验；退出时递增/删除；测试旧 Cookie 退出后 401。
- 说明：这不是“无需凭据的认证绕过”，前提是攻击者已持有 Cookie；但正式交付应满足会话撤销语义。

**P2-02 API 搜索无限流，可绕过 Web 保护**

- API 路由 [Program.cs:632-701](../../src/WhalePod.Api/Program.cs#L632-L701) 未附加 `RequireRateLimiting`，也未创建查询超时；Web 的 `public-search` 策略在 [Program.cs:307-320](../../src/WhalePod.Web/Program.cs#L307-L320) 配置为默认 30 次/分钟、3 秒超时。
- 动态验证：同一 IP 对 API `/api/v1/search` 连续 35 次全部 200；同一时刻 Web `/search` 为 30×200 + 5×429。
- 影响：匿名者切换到 API 即可避开 Web 限流与超时，增加数据库资源滥用面；SRS REQ-NF-016 要求搜索有限流。
- 建议：Web/API 共享搜索限流策略与超时；在 `PublicSearchService` 内统一创建 `CancellationTokenSource`；429 返回 `Retry-After`。

**P2-03 非法游标触发 500**

- [PublicContentService.cs:1007-1018](../../src/WhalePod.Infrastructure/Services/PublicContentService.cs#L1007-L1018) 与 [PublicContentService.cs:1065-1077](../../src/WhalePod.Infrastructure/Services/PublicContentService.cs#L1065-L1077) 用 `new DateTimeOffset(ticks, TimeSpan.Zero)` 构造时间，但 catch 列表没有 `ArgumentOutOfRangeException`。
- 动态验证：构造 `{"s":"latest","t":-1,"i":"..."}` 的 base64url 游标，请求 `/?cursor=...` 返回 **500**。
- 影响：公开参数即可触发未处理异常；日志被噪声污染，错误页对用户不友好。
- 建议：显式校验 ticks 范围（`DateTimeOffset.MinValue.UtcTicks..MaxValue.UtcTicks`），统一返回 400；补充负数、超大值、错误编码、scope 不匹配测试。

**P2-04 提交失败不保留输入**

- 评论失败：[Program.cs:1208-1210](../../src/WhalePod.Web/Program.cs#L1208-L1210) 只重定向到 `?error=...`；[PostDetail.razor:301-304](../../src/WhalePod.Web/Components/Pages/PostDetail.razor#L301-L304) 的 textarea 没有回填，也没有本地草稿。
- 发帖失败：[Program.cs:1298-1307](../../src/WhalePod.Web/Program.cs#L1298-L1307) 只带错误字段重定向；Editor 没有服务端回填。JS 本地草稿（[App.razor:243-367](../../src/WhalePod.Web/Components/App.razor#L243-L367)）可以手动“继续编辑”，但仅覆盖发帖编辑器，且无 JS 时完全丢失。
- 动态验证：评论输入 1 个字符提交失败后文本框为空；发帖填写标题/正文/内核 + 非法图片 ID 提交失败后四个字段均为空（草稿按钮存在，可手动恢复）。
- 影响：违反 SRS F-032 AC4；评论场景直接丢失用户输入。
- 建议：服务端安全回填（一次性、用户隔离的暂存或原表单回显）；评论也提供同等的本地草稿；错误与字段关联并聚焦；提交前同步保存草稿。

**P2-05 评论分页与深层回复**

- [PostDetail.razor:380-382](../../src/WhalePod.Web/Components/Pages/PostDetail.razor#L380-L382) 的 `ReplyToComment` 只在当前页 `Comments.Items` 中查找；[PostDetail.razor:450](../../src/WhalePod.Web/Components/Pages/PostDetail.razor#L450) 的“回复”链接没有保留 `cursor`，第二页回复会回到第一页并丢失 `parentId`。
- [PostDetail.razor:462](../../src/WhalePod.Web/Components/Pages/PostDetail.razor#L462) 只渲染 `depth < 6`；[CommentWriteService.cs:54-68](../../src/WhalePod.Infrastructure/Services/CommentWriteService.cs#L54-L68) 没有对应深度限制。
- 影响：用户回复对象被静默改变为根评论；第 7 层及更深的已审核评论计入数量但不可见。
- 建议：回复时独立读取目标评论（校验同帖、可见、未删除），或在分页链接中保留回复上下文；视觉缩进可限制，内容不应截断；目标失效时明确报错并保留输入。

**P2-06 无 JS 时危险操作没有确认**

- [Moderation.razor:129](../../src/WhalePod.Web/Components/Pages/Admin/Moderation.razor#L129) 的“快速通过”直接提交，没有确认；[ModerationDetail.razor:146](../../src/WhalePod.Web/Components/Pages/Admin/ModerationDetail.razor#L146) 依赖 `onsubmit="return confirm(...)"`。
- 服务端 [AdminConsoleEndpoints.cs:137-164](../../src/WhalePod.Web/AdminConsoleEndpoints.cs#L137-L164) 没有校验确认字段。
- 影响：无 JS 或脚本被拦截时，误点击可直接公开内容/改变用户状态；SRS F-062/F-113 的操作确认要求没有可靠实现。
- 建议：采用原生确认字段 + 服务端校验（或独立确认页），与已有分类/站点模式确认机制一致；“快速通过”至少加一次显式确认。

### 6.2 业务状态机与配置

**P2-07 已采纳评论编辑/驳回后仍保持 solved**

- [CommentWriteService.cs:179-186](../../src/WhalePod.Infrastructure/Services/CommentWriteService.cs#L179-L186)：普通作者编辑后置 `Pending`，没有清除 `IsAccepted`/帖子 `AcceptedCommentId`。
- [ContentModerationService.cs:813-824](../../src/WhalePod.Infrastructure/Services/ContentModerationService.cs#L813-L824)：只有 `Hide/Delete` 清除采纳，`Reject` 不清除。
- 公开查询会隐藏 pending/rejected 评论，但帖子仍以 `IsSolved` 筛选并展示“已采纳”状态；已有采纳 ID 还会阻止采纳另一答案。
- 影响：帖子显示“已解决”但没有可见答案；作者无法改采纳，读者得到错误信息。
- 建议：评论离开 `Approved` 时统一事务性清除双方采纳字段（包括编辑→Pending、Reject），并通知/审计；编辑→再次 Approved 是否恢复采纳需产品明确。
- 验收：编辑→Pending、Reject、再次 Approve 三种路径下，帖子/评论/公开视图状态一致。

**P2-08 申诉复核说明长度与数据库约束冲突**

- [AppealService.cs:255-259](../../src/WhalePod.Infrastructure/Services/AppealService.cs#L255-L259) 允许 1–1,000 字；接受内容申诉时把 `note` 写入 `ModerationAction.Reason`：[AppealService.cs:313-325](../../src/WhalePod.Infrastructure/Services/AppealService.cs#L313-L325) / [:362-374](../../src/WhalePod.Infrastructure/Services/AppealService.cs#L362-L374)。
- `ModerationAction.Reason` 为 `varchar(500)`：[WhalePodDbContext.Moderation.cs:34](../../src/WhalePod.Infrastructure/Persistence/WhalePodDbContext.Moderation.cs#L34)、迁移 [20261025021714_InitialCreate.cs:92](../../src/WhalePod.Infrastructure/Persistence/Migrations/20261025021714_InitialCreate.cs#L92)。
- 影响：501–1,000 字的合法输入通过校验后在保存时抛数据库长度错误，整事务回滚；账号申诉/驳回路径又没有同样的限制，行为不一致。
- 建议：统一输入与存储契约（扩列或限制到 500），返回明确验证错误而不是落库异常；补 500/501/1000/1001 边界测试。

**P2-09 申诉目标删除后无法结案，可堵塞队列**

- [AppealService.cs:281-300](../../src/WhalePod.Infrastructure/Services/AppealService.cs#L281-L300) 在申诉人/内容/父帖删除时抛 `NotFound`，接受和驳回都走同一路径；[AppealService.cs:208-214](../../src/WhalePod.Infrastructure/Services/AppealService.cs#L208-L214) 队列只取最旧 50 条，无分页。
- 作者可以软删除已提交申诉的驳回帖子：[PostWriteService.cs:310-335](../../src/WhalePod.Infrastructure/Services/PostWriteService.cs#L310-L335)。
- 影响：失效申诉永久占用队列头部；累计 50 条后，后续正常申诉不再出现。
- 建议：为失效目标提供“撤回/目标不存在”结案动作，或允许填写原因后驳回；队列增加分页/游标。

**P2-10 敏感词策略未接入写入/审核**

- [SensitiveWordAdminService.cs:97-107](../../src/WhalePod.Infrastructure/Services/SensitiveWordAdminService.cs#L97-L107) 与 [:229-285](../../src/WhalePod.Infrastructure/Services/SensitiveWordAdminService.cs#L229-L285) 只负责增删改查与审计；全仓未发现 `PostWriteService`/`CommentWriteService`/`ContentModerationService` 读取敏感词表或 `Action`。
- SRS REQ-F-063/REQ-F-068 明确要求“命中即人工审核/命中即拒绝”，且策略变更无需重新部署。
- 影响：管理员配置“拒绝”后内容仍进入普通 pending，人工仍可批准；配置界面与实际行为不一致（人工兜底仍在，因此不是自动公开漏洞）。
- 建议：在创建/编辑/审核入口执行本地匹配，记录命中词与策略版本；未实现前从管理界面撤下“拒绝”选项或明确标注“仅记录、未生效”。

**P2-11 “最近活跃”不随评论/采纳更新**

- 生产代码中 `LastActivityAt` 唯一写入点是发帖：[PostWriteService.cs:108](../../src/WhalePod.Infrastructure/Services/PostWriteService.cs#L108)；评论创建/批准、采纳/取消均未更新。
- [PublicContentService.cs:653-664](../../src/WhalePod.Infrastructure/Services/PublicContentService.cs#L653-L664) 的 `active` 排序完全依赖该字段。
- 影响：有新增评论/采纳的帖子不会在“最近活跃”中上浮；排序与用户认知不符。
- 建议：在公开互动事件（评论批准、采纳/取消）事务内更新 `LastActivityAt`，防止并发回退；待审评论不应顶帖。

**P2-12 板块显示名决定“抄作业”规则**

- [PostWriteService.cs:488-491](../../src/WhalePod.Infrastructure/Services/PostWriteService.cs#L488-L491) 以 `category.Name == "抄作业"` 决定 `kernelVersion` 必填；编辑路径 [PostWriteService.cs:229](../../src/WhalePod.Infrastructure/Services/PostWriteService.cs#L229) 同样如此。
- [CategoryAdminService.cs:155](../../src/WhalePod.Infrastructure/Services/CategoryAdminService.cs#L155) 允许修改 `Name`。
- 影响：管理员改展示名即可改变发帖约束；其他板块改成同名会意外触发限制。
- 建议：用稳定板块 ID/Slug 或显式策略字段承载规则；展示名与业务规则解耦。

### 6.3 Agent、设备与邮件

**P2-13 不同 challenge 并发完成可突破活跃设备上限**

- [DeviceVerificationService.cs:350-382](../../src/WhalePod.Infrastructure/Services/DeviceVerificationService.cs#L350-L382) 在事务外/内先 `CountActiveAsync`，再插入新设备；没有锁定用户行，也没有“每用户活跃设备数”约束。
- 影响：账号只剩 1 个名额时，两个不同挑战/公钥可同时通过检查并各自插入，最终超过 `MaxActiveDevices`。
- 建议：事务内 `SELECT ... FOR UPDATE` 锁用户行，或在数据库层用唯一约束/计数表保证上限；补双 DbContext 并发测试。

**P2-14 Agent 预算非原子，内容额度会阻断已领取任务**

- [AgentModerationService.cs:194-198](../../src/WhalePod.Infrastructure/Services/AgentModerationService.cs#L194-L198)、[:471-539](../../src/WhalePod.Infrastructure/Services/AgentModerationService.cs#L471-L539)、[:645-698](../../src/WhalePod.Infrastructure/Services/AgentModerationService.cs#L645-L698)、[:735-778](../../src/WhalePod.Infrastructure/Services/AgentModerationService.cs#L735-L778)；[AiBudgetService.cs:362-387](../../src/WhalePod.Infrastructure/Services/AiBudgetService.cs#L362-L387)、[:414-433](../../src/WhalePod.Infrastructure/Services/AiBudgetService.cs#L414-L433)。
- 每日任务额度为 1 时，两个已领取任务并发提交都读到建议数 0，均可成功；内容额度为 1 时，列表生成任务后计数达到 1，claim/suggestion 随即被同一额度拒绝，导致“任务已生成但永远无法完成”。
- 建议：原子预留/结算任务与 token 配额；内容额度只限制“新任务生成”，不阻断已预留任务的领取/提交；批量生成按剩余额度截断。

**P2-15 Agent 释放租约可清除新租约/状态回退**

- [AgentModerationService.cs:398-432](../../src/WhalePod.Infrastructure/Services/AgentModerationService.cs#L398-L432) 读取 tracked 实体后直接改 `Status/Claimed*` 并 `SaveChanges`；没有实例/租约代次条件，`ModerationTask` 也没有并发令牌。
- [AgentModerationService.cs:1207-1217](../../src/WhalePod.Infrastructure/Services/AgentModerationService.cs#L1207-L1217) 的 `ReleaseTaskLeaseAsync` 只检查 `Status == Claimed`，不检查实例/租约代次。
- 影响：过期请求可把另一实例刚领取的租约清空，或把已 `suggested` 的任务退回 `queued`；造成重复处理、合法持有人丢失租约。
- 建议：引入 `LeaseToken/Generation`，所有释放/完成使用带实例、代次、状态的原子条件更新；读取后保存改为 `ExecuteUpdate`。

**P2-16 MCP 超时/取消会终止会话且退出码为 0**

- [McpStdioServer.cs:47](../../src/WhalePod.Agent/McpStdioServer.cs#L47)、[:72-75](../../src/WhalePod.Agent/McpStdioServer.cs#L72-L75)、[:165-169](../../src/WhalePod.Agent/McpStdioServer.cs#L165-L169)、[:313-328](../../src/WhalePod.Agent/McpStdioServer.cs#L313-L328)；[Program.cs:38-42](../../src/WhalePod.Agent/Program.cs#L38-L42)、[:60-63](../../src/WhalePod.Agent/Program.cs#L60-L63)。
- `OperationCanceledException` 绕过工具错误转换，结束整个循环；顶层捕获后返回 0。清理阶段的取消还会中断后续租约释放。
- 影响：一次 API 超时就让 MCP Server 静默退出，宿主误以为正常结束；租约可能残留。
- 建议：区分请求取消、传输超时和进程退出；每个请求独立 CTS；清理逐项容错并限时；故障退出返回非零。

**P2-17 Agent 允许向任意 HTTP 地址发送 Bearer**

- [AgentCliOptions.cs:32-38](../../src/WhalePod.Agent/AgentCliOptions.cs#L32-L38) 接受任意 `http/https` 绝对地址；[AgentApiClient.cs:209-233](../../src/WhalePod.Agent/AgentApiClient.cs#L209-L233) 首次请求即附带 Bearer。
- 影响：运维误配为非回环 HTTP 时，服务令牌与待审内容明文传输；后续重定向到 HTTPS 也不能保护首次请求。
- 建议：默认强制 HTTPS；仅显式开发模式允许回环 HTTP；在发出任何网络请求前校验。

**P2-18 产物名称未脱敏**

- [AgentModerationService.cs:1034-1052](../../src/WhalePod.Infrastructure/Services/AgentModerationService.cs#L1034-L1052) 对 `artifact.Content`、`artifact.Note` 调用 `RedactInto`，但 `artifact.Name` 原样进入 `AgentTaskArtifactView`。
- 影响：用户把邮箱/疑似 token 放进合法产物名称时，会原样发送给本机 Agent/MCP；违反 REQ-F-129 AC2 的“疑似密钥/令牌脱敏”。
- 建议：所有用户可写文本字段统一脱敏；补名称/备注/正文同一样本的断言。

**P2-19 Agent 任务游标毫秒截断导致重复页**

- [AgentModerationService.cs:1520-1537](../../src/WhalePod.Infrastructure/Services/AgentModerationService.cs#L1520-L1537) 用 `ToUnixTimeMilliseconds()` 编码 `CreatedAt`；查询用原始 `CreatedAt > cursor` 比较。
- 同优先级任务的 `CreatedAt` 含亚毫秒部分时，上一页末项仍满足 `>` 条件，`limit=1` 可持续返回同项/同游标。
- 建议：游标保留数据库实际精度（如 ticks/微秒）或统一排序精度；补微秒时间戳遍历测试。

**P2-20 非法 JSON/未知字段不进入失败熔断**

- [AgentEndpoints.cs:97-120](../../src/WhalePod.Api/Endpoints/AgentEndpoints.cs#L97-L120) 的反序列化在计数 try/catch 之外；[ApiSupport.cs:87-110](../../src/WhalePod.Api/Endpoints/ApiSupport.cs#L87-L110) 只有 `SUGGESTION_INVALID` 被记录。
- 影响：服务账号连续提交损坏 JSON/未知字段时，不会触发文档承诺的连续失败熔断。
- 建议：把建议端点的解析失败纳入脱敏失败记录；认证错误不计入。

**P2-21 Device CLI 的 `uname` 超时无效**

- [DeviceCli/Program.cs:292-309](../../src/WhalePod.DeviceCli/Program.cs#L292-L309) 先同步 `StandardOutput.ReadToEnd()`，之后才 `WaitForExit(2000)`；stderr 未消费。
- 影响：PATH 中的 `uname` 挂起或保持 stdout 打开时，CLI 永远不会到达超时检查，违反退出码契约。
- 建议：异步读取双流 + 整体超时；超时后终止子进程并降级；补挂起/大量 stderr 的替身测试。

**P2-22 邮件 outbox 租约无 fencing**

- [EmailDispatchService.cs:35-173](../../src/WhalePod.Infrastructure/Email/EmailDispatchService.cs#L35-L173) 整批使用统一租期，发送结果无条件 `SaveChanges`；[:185-223](../../src/WhalePod.Infrastructure/Email/EmailDispatchService.cs#L185-L223) 过期回收直接把 `sending` 改回 `pending`；`EmailOutbox` 没有并发令牌。
- 影响：A 实例发送阻塞到租约过期，B 重新领取并写 `sent`，A 的失败结果随后可把同一行改回 `pending/failed`；成功记录与重试上限不再可信。这不是“at-least-once 允许的重复发信”，而是终态倒退。
- 建议：租约代次 + 条件完成更新（`WHERE id=@id AND lease_token=@token`），逐条领取或续租；双 dispatcher + 可控时钟测试。

**P2-23 媒体清理可被最早失败批次饿死**

- [MediaCleanupService.cs:24-45](../../src/WhalePod.Infrastructure/Services/MediaCleanupService.cs#L24-L45) 固定取最早 N 条，失败只记日志并保留原排序，没有 `nextAttempt/backoff/quarantine`。
- 影响：最早一批持续删除失败时，每个周期重复同一批，后续可删除对象永远不处理，24 小时临时媒体保留失效。
- 建议：持久化重试时间与退避，失败对象隔离/告警；保证后续条目能推进。

**P2-24 备份状态与健康检查误报**

- [BackupStatusProvider.cs:33-62](../../src/WhalePod.Infrastructure/Services/BackupStatusProvider.cs#L33-L62) 不校验 JSON 根/字段类型，只捕获 IO/Unauthorized/Json；[SystemHealthService.cs:263-270](../../src/WhalePod.Infrastructure/Services/SystemHealthService.cs#L263-L270) 只检查时间是否存在，不检查新鲜度；[:321-351](../../src/WhalePod.Infrastructure/Services/SystemHealthService.cs#L321-L351) 将其他检查异常统一解释为数据库不可达。
- 影响：备份停更数周不会告警；合法 JSON 但错误类型可遮蔽全部健康指标；数据库指标可能被误报。
- 建议：校验结构、时间范围与最大备份年龄；组件独立降级；补超过 24 小时、未来时间、数组根、错误字段类型测试。

**P2-25 评论审核通知/邮件缺少目标链接**

- [ContentModerationService.cs:877-907](../../src/WhalePod.Infrastructure/Services/ContentModerationService.cs#L877-L907) 的评论审核负载没有 `postId/targetUrl`；[NotificationPresentation.cs:110-136](../../src/WhalePod.Infrastructure/Services/NotificationPresentation.cs#L110-L136) 只为帖子/回复/采纳生成回退链接；[EmailTemplateRenderer.cs:28](../../src/WhalePod.Infrastructure/Email/EmailTemplateRenderer.cs#L28) / [:44-48](../../src/WhalePod.Infrastructure/Email/EmailTemplateRenderer.cs#L44-L48) URL 为空就省略详情入口。
- 影响：评论通过/驳回通知无法跳转到对应目标，用户只能自己搜索。
- 建议：生成带帖子 ID 与评论锚点的内部链接；通知/邮件测试断言目标存在且鉴权正确。

### 6.4 运维、测试与发布

**P2-26 `/healthz/ready` 只验证可连接**

- [Api/Program.cs:343-355](../../src/WhalePod.Api/Program.cs#L343-L355)、[Web/Program.cs:420-433](../../src/WhalePod.Web/Program.cs#L420-L433) 仅执行 `CanConnectAsync`；空库、旧 schema、迁移未应用都会返回 ready。
- 建议：检查迁移版本/关键表/扩展，或至少执行只读探测查询；切流验收应包含迁移兼容检查。

**P2-27 迁移 Down 在已有新数据时失败**

- [20261025083022_AddAppealResultNotificationType.cs:24-33](../../src/WhalePod.Infrastructure/Persistence/Migrations/20261025083022_AddAppealResultNotificationType.cs#L24-L33) 的 Down 重建旧 CHECK，已有 `appeal_result` 通知时会失败。
- [20261026053230_AddAvatarAndGitHubProfileSupport.cs:31-44](../../src/WhalePod.Infrastructure/Persistence/Migrations/20261026053230_AddAvatarAndGitHubProfileSupport.cs#L31-L44) 的 Down 删除头像字段，既有头像数据会丢失。
- 建议：在含新枚举/新字段的演练集上验证回退；明确“应用回滚、数据库降级、备份恢复”三种策略及数据损失窗口。

**P2-28 测试计数与门禁**

- `tests/WhalePod.Tests.Unit/UnitTest1.cs`、`tests/WhalePod.Tests.Integration/UnitTest1.cs`、`tests/WhalePod.Tests.EndToEnd/UnitTest1.cs` 各有一个空测试（`:5-9`）。
- HTTP factory 强制 `UseEnvironment("Development")`：[ApiHttpTests.cs:24](../../tests/WhalePod.Tests.Integration/ApiHttpTests.cs#L24)、[:84](../../tests/WhalePod.Tests.Integration/ApiHttpTests.cs#L84)，生产启动校验从未被测试。
- Release 工作流没有 TRX 跳过门禁：[release.yml:56-63](../../.github/workflows/release.yml#L56-L63)。
- 建议：移除空占位计分；增加 Production 启动矩阵、契约对账、脚本失败路径、带数据迁移演练；CI/Release 统一“0 跳过”门禁。

**P2-29 构建不可完全复现**

- [global.json:3-4](../../global.json#L3-L4) 使用 `latestFeature`；CI 安装 `10.0.x`，runner/PostgreSQL 镜像/Actions 使用可变标签；无依赖锁文件或 locked-mode。
- 影响：同一提交在不同时间可能解析到不同 SDK/依赖/镜像；发布产物与验证产物不保证同一解析结果。
- 建议：固定 SDK/依赖/镜像/Action 版本；保存依赖清单与产物来源；在发布矩阵复用同一 restore 结果。

**P2-30 负载脚本不能作为 10 分钟容量门禁**

- `tests/load/public-read.js:42-60`、`copy-counter-tiers.js:14-19,38-40`、`upload-images.js:36-40` 固定 20–60 秒，无 checks/阈值/丢弃迭代门槛；上传脚本依赖开发登录（生产禁止）。
- 建议：支持时长与正式测试身份注入；同时校验吞吐、丢弃迭代、业务响应、数据库计数与 SRS 延迟指标。

**P2-31 交付声明与证据链不一致**

- `docs/design/traceability.md:20` 称 20 个 UAT“已执行”；`docs/design/acceptance.md:43` 明确 UAT-15 未执行，且多项为“部分”；历史构建/axe/恢复结论没有关联提交、命令、原始证据地址与签署人。
- 配置参考漏列 `DeviceVerification`、`AgentRateLimit` 等实际运行项。
- 建议：建立 REQ→实现→测试→原始结果→提交/产物→责任人的追踪；严格区分“未实现/待环境/已执行失败/已通过/批准豁免”。

**P2-32 生产发布链路缺可执行产物**

- [release.yml:73-122](../../.github/workflows/release.yml#L73-L122) 只产出 Web/API framework-dependent ZIP 与三平台 Device CLI；没有镜像、部署清单、迁移发布产物、定时备份配置、对象/DP 一致恢复步骤。
- `scripts/*.sh` 的 Git 模式为 `100644`（`git ls-files -s` 确认），文档中的 `./scripts/...` 在常规 Linux 检出后不可直接执行。
- 建议：补齐镜像/部署/迁移/备份调度/回滚的仓库侧产物；脚本加可执行位；在干净目标机按交付物演练。

---

## 7. P3 与已知限制

| ID | 问题 | 证据/说明 | 建议 |
|---|---|---|---|
| P3-01 | 正常页面没有退出登录入口 | 浏览器检查 header/菜单只有个人主页、设置、申诉、管理；`/logout` 表单只在 `Activate.razor:34-37` | 在用户菜单加入“退出登录”，保留 CSRF |
| P3-02 | 封禁后旧 Cookie 可读自己的 pending/rejected 详情 | [Api/Program.cs:463-474](../../src/WhalePod.Api/Program.cs#L463-L474) 只重新核验管理员；[PublicContentService.cs:750-794](../../src/WhalePod.Infrastructure/Services/PublicContentService.cs#L750-L794) 按作者 ID 放行 | 作者特权读取前核验当前账号状态；仅确认自己的内容，风险有限 |
| P3-03 | 安全响应头不完整，`AllowedHosts="*"` | 实际响应只有 `Content-Security-Policy: frame-ancestors 'self'` 与 `X-Frame-Options: SAMEORIGIN`，没有 `script-src/default-src` 级 CSP，也没有 `Referrer-Policy`；`appsettings.json:8` 为 `*` | 增加实质 CSP（先 report-only）与 Referrer-Policy、Host 白名单 |
| P3-04 | 生产邮件根 URL 允许 HTTP，SMTP 可配 `None` | [EmailServiceCollectionExtensions.cs:102-129](../../src/WhalePod.Infrastructure/Email/EmailServiceCollectionExtensions.cs#L102-L129) 只校验 HTTP(S)；[EmailSender.cs:113-121](../../src/WhalePod.Infrastructure/Email/EmailSender.cs#L113-L121) 接受 `none` | 生产拒绝非 HTTPS 外部地址、明文/降级传输 |
| P3-05 | 单实例浏览去重存在冷缓存并发重复计数 | [ViewCounterService.cs:48-68](../../src/WhalePod.Infrastructure/Services/ViewCounterService.cs#L48-L68) 缓存检查与 DB 递增之间无原子占位 | 每键原子占位/异步互斥，或持久唯一去重 |
| P3-06 | Sitemap 固定 5,000 条上限 | [PublicDiscoveryService.cs:29-76](../../src/WhalePod.Infrastructure/Services/PublicDiscoveryService.cs#L29-L76)；[Web/Program.cs:1361](../../src/WhalePod.Web/Program.cs#L1361) | 提供 sitemap index 与有界分片 |
| P3-07 | 巨型文件/重复组合根 | `Web/Program.cs` 2,581 行、`AgentModerationService.cs` 1,629 行；两个组合根重复注册 16 种服务 | 抽取宿主共享注册与端点模块；按用例拆分 Agent 服务 |
| P3-08 | `Contracts` 名不副实 | 只有 `DeviceProtocol` 1 个文件/300 行；HTTP DTO/错误码在 Infrastructure；Agent 手写 JSON | 明确契约边界，或重命名项目避免误导 |
| P3-09 | 会话 Cookie 8 小时滑动、无绝对过期 | [Web/Program.cs:216-235](../../src/WhalePod.Web/Program.cs#L216-L235) | 增加绝对过期/空闲超时与主动撤销 |
| P3-10 | 站点地图/信息流一次性读全量 | 公开侧栏渲染全部板块、feed 投影完整实体；README 已披露分类侧栏限制 | 分页/缓存；用执行计划与压测验证 |
| P3-11 | axe/浏览器验收资产不在仓库 | `grep` 未找到 axe/Playwright 配置；README 的 56 次 axe 扫描无原始报告/命令 | 将可访问性脚本与基线报告纳入仓库/CI |
| P3-12 | 反序列化失败不计入 Agent 熔断 | 见 P2-20 | 同一修复 |

> 已知限制（非本轮新增缺陷）：外部机审供应商、@提及、标签缓存失效、管理员手动重发邮件、投诉反馈、多管理员复核未实现；浏览量去重仅进程内；AI/MCP 默认关闭；这些在 README/acceptance 中已有披露，本报告不重复计为缺陷。

---

## 8. 整改路线（建议顺序）

### 阶段 A：交付阻断（P1，建议 1–2 周）

1. **修复激活页静态资源门禁**（P1-01）：先做，影响每个新用户；用未激活用户 E2E 验证 CSS/JS 200。
2. **补齐 Web 核心闭环**（P1-02）：编辑/重提、点赞/采纳/举报/删除、图片上传 UI、5 份产物；优先无 JS 表单。
3. **接通生产管理员引导**（P1-03）：OAuth 回调调用 `EnsureAdminAsync`，加生产启动/stub OAuth 测试。
4. **修正申诉与设备状态机**（P1-04/P1-05）：申诉绑定处罚/裁决代次；设备撤销去重键加激活周期。
5. **补齐注销媒体生命周期**（P1-06）：删除内容时清理 PostImage/MediaUpload；增加对象删除队列与审计。
6. **封堵数据安全脚本**（P1-07）：测试库精确校验；恢复改用新库或 `--single-transaction`；传递 SSL 参数。
7. **对齐 OpenAPI**（P1-08）：实现或删除 7 个管理操作，加契约测试。

### 阶段 B：发布前重要缺陷（P2，建议 1–2 个迭代）

1. 会话撤销（P2-01）与 API 搜索限流/超时（P2-02）、游标 400（P2-03）。
2. 表单失败回填（P2-04）、评论分页/深度（P2-05）、无 JS 确认（P2-06）。
3. 采纳状态清理（P2-07）、申诉长度/队列（P2-08/P2-09）、敏感词接线（P2-10）、活跃时间（P2-11）、板块规则（P2-12）。
4. 设备上限原子性（P2-13）、Agent 预算/租约/取消（P2-14/P2-15/P2-16）、HTTP/脱敏/游标/熔断（P2-17–P2-20）、CLI 超时（P2-21）、outbox fencing（P2-22）、媒体清理退避（P2-23）、健康/通知（P2-24/P2-25）。
5. 测试门禁、Production 启动矩阵、锁文件、负载门禁、交付证据链（P2-26–P2-32）。

### 阶段 C：持续加固（P3）

安全头/Host 白名单、退出入口、封禁读取状态、Sitemap 分片、组合根去重、巨型文件拆分、契约边界、可访问性资产入库、浏览去重持久化。

---

## 9. 限制与未验证项

1. **生产环境未验证**：真实 GitHub OAuth、SMTP、COS/S3、CDN/WAF、腾讯云预发迁移/回滚、Data Protection 共享目录权限、代理头可信网络。
2. **外部客户端未验证**：真实屏幕阅读器、物理 iOS/Android/iPad、macOS/Linux Device CLI 实机、签名/公证。
3. **容量未验证**：本轮只做了小规模 API/Web 限流对比，未执行 10 分钟压测；不能据此判断 SRS 容量指标。
4. **并发复现边界**：设备上限、Appeal 交叉状态、Agent 预算/游标、outbox fencing 等已按源码路径确认，并由隔离复现任务补充验证；未完成的项在表中标为“静态确认”。
5. **安全边界**：未做渗透测试/SAST/DAST；未验证对象存储桶策略、CDN 缓存撤销、SMTP 退信分类、备份加密与异地副本。
6. **文档证据**：README/acceptance 中的历史 axe、UAT、恢复结论没有原始命令与产物；本报告未将其视为本轮通过。
7. **未发现 ≠ 不存在**：本报告未发现 P0，不等于系统不存在未被覆盖的漏洞；正式上线前仍应完成独立安全评审与预发演练。

---

## 10. 附录

### 10.1 审查覆盖与统计

| 范围 | 代码文件数 | 物理行数 |
|---|---:|---:|
| Agent | 5 | 1,096 |
| API | 15 | 2,578 |
| Contracts | 1 | 300 |
| DeviceCli | 1 | 406 |
| Domain | 7 | 737 |
| Infrastructure（不含迁移） | 76 | 18,111 |
| Web | 73 | 14,147 |
| **src 非迁移代码** | **178** | **37,375** |
| 迁移 Up/Down | 5 | 1,873 |
| Designer/Snapshot | 6 | 17,370 |
| **src 含迁移** | **189** | **56,618** |
| Unit 测试 | 6 | 649 |
| Integration 测试 | 55 | 14,859 |
| EndToEnd 测试 | 1 | 10 |
| **tests 代码** | **66** | **15,772** |

统计口径：实际 `os.walk`，排除 `bin/obj/node_modules/.git/.vs/.qoder-credits/artifacts`；物理行包含空行/注释；`.cs/.razor/.js/.css` 计入。测试声明：Unit 23 Fact；Integration 158（156 DatabaseFact + 2 Fact）；EndToEnd 1 Fact；共 182，其中 3 个为空占位。

### 10.2 可复现命令

```bash
# 构建
dotnet build WhalePod.slnx -c Release --no-restore

# 独立 PostgreSQL（示例：本报告使用 127.0.0.1:55438，库名必须包含 whalepod_test）
createdb -h 127.0.0.1 -p 55438 -U review whalepod_test_review
WHALEPOD_TEST_DB_CONNECTION="Host=127.0.0.1;Port=55438;Database=whalepod_test_review;Username=review" \
  dotnet test WhalePod.slnx -c Release --no-build --no-restore

# 格式
dotnet format WhalePod.slnx --verify-no-changes --no-restore

# 依赖漏洞
dotnet list WhalePod.slnx package --vulnerable --include-transitive

# 备份/恢复（仅隔离库）
WHALEPOD_DB_CONNECTION="Host=127.0.0.1;Port=55438;Database=whalepod_browser_review;Username=review" \
  bash scripts/backup-postgres.sh
WHALEPOD_DB_CONNECTION="Host=127.0.0.1;Port=55438;Database=whalepod_browser_review;Username=review" \
  bash scripts/restore-postgres.sh --backup <dump> --database <newdb> --confirm-database <newdb> --yes
```

### 10.3 原始证据索引

| 文件 | 内容 |
|---|---|
| `artifacts/review-20261028/build.log` | Release 构建结果 |
| `artifacts/review-20261028/integration-tests.log` | 158/158 集成测试 |
| `artifacts/review-20261028/verified-test-results/*.trx` | 集成测试 TRX |
| `artifacts/review-20261028/browser-evidence.json` | 浏览器检查与失败项 |
| `artifacts/review-20261028/pending-activation.png` | 激活页无样式截图 |
| `artifacts/review-20261028/post-desktop.png` / `post-mobile-no-js.png` | 发帖审核闭环桌面/移动无 JS |
| `artifacts/review-20261028/rss-before.*` / `rss-after.*` | RSS 304 撤回验证 |
| `artifacts/review-20261028/upload.json` / `upload.headers` | 临时图片上传与匿名读取 |
| `artifacts/review-20261028/backup.log` / `restore.log` | 备份/恢复验证 |

### 10.4 审查边界

- 本次未修改任何产品源码、测试、CI 或配置；工作区 `git status --short` 为空（报告与 artifacts 为新增未跟踪文件）。
- 所有数据库操作仅针对本次新建的隔离实例/库；未触碰 `127.0.0.1:55432`。
- 未发送真实邮件、未调用 SMTP/COS/S3/云 API、未上传任何项目内容到外部服务。

---

**报告结论：** WhalePod v0.1.0 的工程底子值得肯定，尤其是真实数据库集成测试、公开可见性过滤、清洗与审计。但“本地验收通过”不能替代“正式交付”：当前仍有 8 项 P1 阻断项，其中激活页资源、Web 核心闭环、生产管理员初始化、申诉/设备状态机与注销媒体生命周期会直接影响真实用户或运营安全。建议按第 8 节顺序完成阶段 A 后再进行生产签收评审。

> 配套整改提示词包见 [fix-prompts/README.md](fix-prompts/README.md)；建议每个阶段新开会话，先粘贴 `fix-prompts/00-master.md`，再执行对应阶段文件。
