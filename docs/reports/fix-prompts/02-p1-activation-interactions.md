# 阶段 1：激活页资源与 Web 互动/编辑闭环

> 先粘贴 `00-master.md`。本阶段覆盖 P1-01 与 P1-02 的“点赞/采纳/举报/评论删除/作者编辑重提”部分；图片上传 UI、5 份产物、标签分页在阶段 2，不要混做。

## 范围

- **P1-01**：未激活用户访问 `/activate` 时，指纹化 CSS、scoped CSS、`ReconnectModal.razor.js` 被 `ActivationGateMiddleware` 302 到 `/activate`，页面无样式、模块脚本 MIME 错误。
- **P1-02（本阶段部分）**：
  - 作者编辑、驳回后查看理由并重提；
  - 帖子/评论点赞、取消点赞；
  - 作者采纳/取消采纳；
  - 成员举报帖子/评论/用户；
  - 作者/管理员删除评论；
  - 所有上述操作在 Web 端有原生表单（无 JS 可用），不是只能调 JSON API。

**不在本阶段**：图片上传 UI、5 份产物、标签 >50（阶段 2）；审核/举报/申诉后台（已存在）；会话撤销（阶段 5）。

## 必读

- 报告：P1-01、P1-02、SRS F-033/F-041/F-050/F-053/F-054/F-055/F-024。
- `src/WhalePod.Web/ActivationGateMiddleware.cs`
- `src/WhalePod.Web/Components/App.razor`
- `src/WhalePod.Web/Components/Layout/ReconnectModal.razor`
- `src/WhalePod.Web/Components/Pages/PostDetail.razor`
- `src/WhalePod.Web/Components/Pages/Editor.razor`
- `src/WhalePod.Web/Program.cs`（相关路由：`/posts/{id}/comments`、`/editor/create`、`/api` 协作方式）
- `src/WhalePod.Web/AdminConsoleEndpoints.cs`（学习现有表单/CSRF/错误映射风格）
- `src/WhalePod.Infrastructure/Services/PostWriteService.cs`（UpdateAsync/Delete）
- `src/WhalePod.Infrastructure/Services/CommentWriteService.cs`
- `src/WhalePod.Infrastructure/Services/LikeService.cs`
- `src/WhalePod.Infrastructure/Services/AcceptanceService.cs`
- `src/WhalePod.Infrastructure/Services/ReportService.cs`
- `src/WhalePod.Infrastructure/Services/AuthorContentService.cs`
- 现有 Web HTTP 测试：`tests/WhalePod.Tests.Integration/PublicPageHttpTests.cs`、`WebFormTestSupport.cs`、`WebProtectionTests.cs`

## 实施要求

### A. P1-01 激活页静态资源

1. 读清中间件顺序：`ActivationGateMiddleware` 在 `MapStaticAssets` 端点之前拦截；`ActivationGateMiddleware` 的允许前缀是固定字符串，无法匹配指纹文件名。
2. 修复方式优先：
   - 仅对“动态页面/写请求”做激活检查；静态资源端点（`MapStaticAssets` 生成的 endpoint metadata）直接放行；
   - 或使用可靠的静态资源判定（路径段 + 已知扩展名 + `_framework`/`_blazor`/`Components/**/*.razor.js`），不要只加几个固定文件名。
3. 不要放行所有请求；未激活用户仍不能访问 `/editor`、`/settings`、写接口等。
4. 测试：
   - 新增 Web 集成测试：未激活用户 `GET /activate` 时，页面引用的 CSS/JS 均 200，`Content-Type` 正确；
   - 断言 `ReconnectModal.*.razor.js`、`app.*.css`、`WhalePod.Web.*.styles.css` 不被 302；
   - 未激活用户 `GET /editor` 仍被重定向/拒绝；激活后恢复正常。
5. 浏览器验证：用未激活账号打开 `/activate`，375px/1280px 截图；控制台无 MIME/404 错误；表单可用。

### B. P1-02 互动与编辑闭环

先完整阅读现有服务签名和权限规则，不要凭报告猜方法名。

#### B1. 作者编辑/重提

- 新增路由（二选一，优先 `/posts/{id}/edit`）：作者/管理员可见；回填标题、板块、正文、标签、环境、图片、产物。
- 表单携带当前 `Version`（隐藏字段）；服务端调用 `PostWriteService.UpdateAsync`（或等价现有方法），使用 `If-Match` 语义；版本冲突返回 409 并保留输入。
- 非管理员作者编辑后进入 `Pending`（现有规则）；管理员编辑不强制重审（按现有规则）。
- 被驳回内容在详情/编辑页展示驳回理由与时间（来源：现有 moderation 记录/通知；不要泄露举报人）。
- 成功后跳转详情并提示“已重新提交审核”；失败保留输入（见阶段 5 的通用输入保留；本阶段至少编辑页不能丢字段）。

#### B2. 点赞/取消点赞

- 帖子详情：登录用户显示“点赞/取消点赞”表单；未登录引导登录；不能赞自己；禁言/封禁由服务端拒绝。
- 使用 `LikeService`（帖子/评论现有方法）；Web 端点必须 `RequireAuthorization` + antiforgery。
- 评论行同样支持点赞/取消；计数从服务端重新读取，不要只改前端数字。

#### B3. 采纳/取消采纳

- 仅帖子作者可见/可用；不能采纳自己的评论（按现有配置/规则）；每帖最多一个。
- 使用 `AcceptanceService`；采纳后帖子“已解决”，评论显示徽标；取消后回退。
- 管理员不能代替作者采纳（SRS F-053；如现有服务允许，按 SRS 收紧并测试）。

#### B4. 举报

- 帖子/评论/用户详情提供举报入口；原因枚举至少：垃圾广告、人身攻击、分类错误、抄袭未注明、其他；可填补充说明。
- 使用 `ReportService`；举报人身份不公开；未登录引导登录；禁言用户可举报（SRS F-005）。
- 成功后提示“已提交，管理员会处理”；重复举报/自身内容等规则按现有服务。

#### B5. 评论删除

- 作者/管理员可删除；删除前确认（无 JS 也要有服务端确认字段或确认页）；删除被采纳评论时提示会取消已解决状态。
- 使用 `AuthorContentService`/`CommentWriteService` 现有删除逻辑；软删除、子评论展示、采纳回退按现有规则。
- 不要物理删除审计/通知。

### C. 表单与错误处理

- 所有写操作使用原生 `<form method="post">` + `<AntiforgeryToken />`（或现有 `ValidateFormAsync` 风格）；不要只靠 `fetch`。
- 统一错误映射：400/403/404/409 → 页面提示；版本冲突保留输入；禁言/未激活/封禁给出明确提示。
- 不把用户输入放进 URL query（除一次性错误码）；不回显敏感内容到日志。
- 保持现有 CSS/组件风格；不引入前端框架。

## 测试与验收

### 自动化

- Web HTTP 测试（真实 PostgreSQL）覆盖：
  - 未登录/未激活/禁言/封禁/非作者/管理员矩阵；
  - 点赞/取消幂等；不能赞自己；
  - 采纳/取消、非作者拒绝、管理员拒绝（按 SRS）；
  - 举报三种目标、原因/说明校验、举报人不可见；
  - 评论删除权限、采纳回退、CSRF；
  - 编辑：作者可编辑、非作者 404/403、版本冲突 409、驳回后重提进入 pending、管理员编辑规则。
- 所有新端点必须验证 CSRF（缺失/错误 token → 400）。
- 运行全量测试，0 failed、0 skipped。

### 浏览器

- 普通成员仅用 UI 完成：发帖 → 驳回 → 查看理由 → 编辑 → 重提；点赞/取消；采纳/取消；举报；删除自己的评论。
- 关闭 JavaScript 后，上述基础路径仍可用（至少点赞、采纳、举报、删除、编辑提交）。
- 375px 与 1280px；控制台无错误；键盘可操作；错误提示可读。

## 停止条件

- 产品决定“互动仅 API、不做 Web UI”：停下来问，并要求同步修改 SRS/traceability/README；不要擅自删需求。
- 编辑需要改变现有审核/版本语义：先说明影响并问。
- 采纳权限与现有服务冲突：按 SRS 提出收紧方案，问是否接受。
- 任何新端点需要放宽 CSRF/授权：拒绝并问。

## 输出

按 `00-master.md` 的阶段结果格式；更新 `fix-log` 中 P1-01、P1-02（互动/编辑部分）。建议 commit message 示例：`fix(web): 修复激活页静态资源并补齐互动编辑入口`。不要 commit/push。
