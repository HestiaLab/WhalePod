# 阶段 2：编辑器上传 UI、5 份产物、标签分页

> 先粘贴 `00-master.md`。本阶段覆盖 P1-02 的剩余部分：图片上传 UI、最多 5 份产物、标签超过 50 个仍可选。不要重做阶段 1 的点赞/采纳/举报/编辑。

## 范围

- SRS F-036：图片上传支持 JPG/PNG/WebP/GIF、≤10 MB、每帖 ≤10 张、上传进度/失败重试、服务端重编码去元数据、上传成功后插入正文。
- SRS F-038：每帖最多 5 份产物；类型 patch/YAML/JSON/bundle/Markdown/shell；名称/内容必填、备注可选；可删除/调整顺序。
- SRS F-025 / F-034（标签）：每帖最多 6 个；当前 Editor 只取第一页 50 项，后续标签不可选。
- 当前编辑器把图片上传降级为“手工调 API + 粘贴 MediaUpload ID”；产物只有 1 份。

## 必读

- 报告 P1-02；SRS F-036、F-038、F-037、F-040。
- `src/WhalePod.Web/Components/Pages/Editor.razor`
- `src/WhalePod.Web/Program.cs` 的 `/editor/create`、`/editor/preview`、表单校验
- `src/WhalePod.Web/Components/App.razor` 的编辑器草稿 JS（只做渐进增强，不要重写）
- `src/WhalePod.Infrastructure/Services/ImageUploadService.cs`
- `src/WhalePod.Infrastructure/Storage/*`（本地/S3）
- `src/WhalePod.Infrastructure/Services/PostWriteService.cs` 的 `NormalizeArtifacts`、`ReplaceImagesAsync`
- `src/WhalePod.Infrastructure/Services/PublicContentService.cs` 的标签分页
- `tests/WhalePod.Tests.Integration/UploadHttpTests.cs`、`PostWriteServiceTests.cs`、`ImageUploadServiceTests.cs`

## 实施要求

### A. 图片上传 UI

1. 编辑器增加图片上传区域：选择文件、显示缩略图/文件名/大小、上传进度、失败重试、删除；每帖最多 10 张。
2. 上传走服务端端点（优先 Web 同站 `/editor/images`，调用现有 `ImageUploadService.UploadAsync`），不要要求用户手工调 API、粘贴 UUID。
3. 端点必须：
   - `RequireAuthorization` + antiforgery；
   - 仅接受 multipart、单文件、大小/类型限制复用现有服务；
   - 返回 `id/url/width/height/mimeType`；
   - 不在 URL/日志中放用户内容；临时上传归属当前用户。
4. 无 JS 退化：至少提供可提交的 `<input type="file">` 表单；上传成功后回到编辑器并把上传 ID 放入隐藏字段/可编辑列表。JS 只负责进度、缩略图、重试、删除等增强。
5. 替代文本：为每张图提供 alt 输入；提交时把 `PostImageInput` 的 alt 传给现有服务；不要固定传 `null`。
6. 图片上传成功后应能“插入正文”（Markdown 图片语法）或至少在正文旁展示可复制的 Markdown；SRS F-036 AC6。
7. 临时上传的 24 小时清理/失败补偿沿用现有服务；不要改成永久对象。

### B. 最多 5 份产物

1. 编辑器改为可管理 5 份产物（固定 5 行或 JS 动态行）；每份字段：类型、名称、备注、内容。
2. 服务端解析为 `List<ArtifactInput>`；最多 5；名称 ≤120、备注 ≤200、内容 ≤200,000；名称/内容必填（按 SRS）。
3. 支持删除、上移/下移/排序；提交顺序即展示顺序；不要用前端 index 直接信任，服务端重新编号/校验。
4. 无 JS 退化：至少 5 组固定字段，用户可填 1–5 份；JS 可隐藏未使用行。
5. 保留现有“产物不执行、纯文本展示”的边界；不要新增执行/预览执行功能。

### C. 标签分页/搜索

1. 当前 `PublicContent.GetTagsAsync(50, null)` 只取第一页；改为：
   - 服务端加载全部（有上限时用循环/游标）或提供标签搜索端点；
   - 确保第 51 个及更早标签可选；
   - 每帖最多 6 个，前端/服务端双重校验。
2. 不要一次渲染数千个标签导致页面膨胀；优先搜索 + 已选列表。若做搜索，必须防抖/取消旧请求，且无 JS 时仍能提交已选标签。
3. 服务端仍按 slug/名称校验标签有效/启用；不要只信前端。

### D. 草稿与错误保留

- 现有本地草稿只覆盖部分字段；扩展草稿时不要包含图片二进制/令牌；只存上传 ID/URL 引用（SRS F-040 AC5）。
- 提交失败时保留所有字段（图片 ID、5 份产物、标签、alt）；无 JS 也要服务端回填或安全暂存。
- 不要为了草稿把私密内容写入 URL/日志。

## 测试与验收

### 自动化

- `ImageUploadService`：现有测试保持；新增 Web 端点测试（未登录/CSRF/过大/非图片/成功）。
- Editor 提交测试：
  - 10 张图片成功、11 张拒绝；
  - 5 份产物成功、6 份拒绝；
  - 产物必填/长度边界；
  - 标签 6 个成功、7 个拒绝；第 51 个标签可选；
  - alt 文本持久化；
  - 失败后字段保留（HTTP 层断言）。
- 运行全量测试，0 failed、0 skipped。

### 浏览器

- 普通成员仅用 UI：选择图片 → 看到进度/缩略图/alt → 提交 → 详情显示图片；失败重试；删除图片。
- 填写 5 份产物、排序、删除；提交后详情按顺序显示；第 6 份被拒绝。
- 选择第 51 个标签；6 个上限生效。
- 关闭 JS：至少可上传 1 张图、填写 1–5 份产物、选择标签并提交；错误后字段保留。
- 375px/1280px；控制台无错误。

## 停止条件

- 需要引入第三方上传组件/前端框架：先问；优先原生 + 现有 CSS/JS。
- 需要改对象存储/桶策略/COS：属于生产配置，先问。
- 上传端点需要放宽鉴权/大小限制：拒绝并问。
- 标签全量加载会影响性能：提出“搜索/分页”方案并问，不要直接一次加载所有。

## 输出

按 `00-master.md` 的阶段结果格式；更新 `fix-log` 中 P1-02（编辑器部分）。建议 commit message 示例：`fix(editor): 补齐图片上传、多产物与标签选择`。不要 commit/push。
