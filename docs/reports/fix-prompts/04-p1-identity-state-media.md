# 阶段 3：OAuth 引导、申诉/设备状态机、注销媒体生命周期

> 先粘贴 `00-master.md`。本阶段覆盖 P1-03、P1-04、P1-05、P1-06。四项都要有回归测试；不要只改一处就宣称全部完成。

## 范围

- **P1-03**：生产 GitHub OAuth 登录没有调用 `AdminBootstrapService.EnsureAdminAsync`，`Auth:BootstrapAdminHandles` 在生产不生效。
- **P1-04**：旧申诉可撤销后来发生的新处罚/新内容裁决。
- **P1-05**：同一设备重新验证后再次撤销，固定 `device-revoked:{id}` 去重键触发通知/邮件唯一约束，撤销事务回滚。
- **P1-06**：注销选择“删除内容”时帖子附图仍 attached，媒体清理永不选中；头像上传与注销竞争可把头像写回已注销用户。

## 必读

- 报告 P1-03～P1-06 及对应证据行。
- `src/WhalePod.Infrastructure/Services/AdminBootstrapService.cs`
- `src/WhalePod.Web/GitHubOAuthEndpoints.cs`、`src/WhalePod.Web/Services/GitHubOAuthService.cs`、`src/WhalePod.Web/Program.cs` 开发登录段
- `src/WhalePod.Infrastructure/Services/AppealService.cs`
- `src/WhalePod.Infrastructure/Services/AdminUserService.cs`
- `src/WhalePod.Infrastructure/Services/PostWriteService.cs` 的编辑/驳回路径
- `src/WhalePod.Infrastructure/Services/DeviceVerificationService.cs`
- `src/WhalePod.Infrastructure/Services/AccountDeletionService.cs`
- `src/WhalePod.Infrastructure/Services/ImageUploadService.cs`
- `src/WhalePod.Infrastructure/Services/MediaCleanupService.cs`
- `src/WhalePod.Infrastructure/Persistence/WhalePodDbContext.Interactions.cs`、`WhalePodDbContext.Communications.cs`
- 相关测试：`LoginHttpTests`、`AppealServiceTests`、`DeviceVerificationServiceTests`、`AccountDeletionServiceTests`、`ImageUploadServiceTests`、`MediaCleanupServiceTests`

## 实施要求

### A. P1-03 OAuth 管理员引导

1. 在 GitHub OAuth 成功创建/更新用户、准备登录的路径调用 `EnsureAdminAsync`；位置必须在用户已持久化、身份可信之后，写审计。
2. 只对 `Auth:BootstrapAdminHandles` 中的 handle 生效；大小写/格式按现有配置规则；重复登录不重复提升。
3. 与现有开发登录行为一致：开发登录仍可引导；生产 OAuth 也必须引导。
4. 明确失败语义：引导失败是否阻断登录？建议阻断并报错（避免无管理员上线）；如选择不阻断，必须告警 + 可重试。
5. 测试（不依赖真实 GitHub）：
   - 用 fake OAuth handler/service 模拟回调，指定 handle 用户被提升为 admin，审计存在；
   - 非指定 handle 不提升；
   - 重复登录不重复写提升审计；
   - 生产配置下 `DevLoginEnabled=false` 仍生效；
   - 真实 OAuth 凭据仍属外部前置，测试用 stub 即可。
6. 生产启动矩阵：补充 `Production` 环境启动测试（至少验证缺 GitHub 配置/密钥时启动失败，配置齐全时可启动）。

### B. P1-04 申诉与后续处罚/裁决

1. 复核 `Appeal.RelatedActionId` 目前只写不读；内容申诉只检查当前 `Rejected`，账号申诉只检查当前 `Muted/Banned`。
2. 修复策略（按最小改动优先）：
   - 申诉提交时绑定具体的处罚/裁决标识与内容版本；
   - 复核时校验当前处罚/裁决仍是该标识/代次；不匹配返回 409/400，并明确“申诉已失效，请重新提交”；
   - 账号处罚新增 `PenaltyId`/代次（如现有模型可复用 `ModerationAction.Id` 则不要重复造字段）；内容使用 `ModerationActionId` + `Version`。
   - 通过时做条件更新，避免并发复核覆盖新处罚。
3. 不接受“只检查当前状态”作为修复；必须能区分“同一处罚”与“后来新处罚”。
4. 测试：
   - 禁言 A → 申诉 A → 新封禁 B → 通过旧申诉：B 保持，旧申诉失效；
   - 驳回内容 → 申诉 → 编辑 → 再驳回 → 通过旧申诉：新版不被恢复；
   - 同一处罚的正常通过仍工作；
   - 复核与新处罚并发：最终状态不得被旧申诉覆盖。
5. 审计：失效/冲突申诉记录原因；不要静默删除申诉。

### C. P1-05 设备重新激活后再次撤销

1. 撤销通知/邮件去重键当前固定 `device-revoked:{device.Id:N}`；改为包含激活周期/撤销代次，例如 `device-revoked:{id}:{activationTicks}` 或维护 `RevocationCount`。
2. 保证同一激活周期内重复撤销幂等；不同周期各自产生通知/邮件。
3. 撤销事务内使用行锁/条件更新；通知/邮件冲突不得导致整个撤销回滚。
4. 测试：
   - 验证 → 撤销 → 用同一密钥重新验证 → 再次撤销：两次都成功，设备最终 revoked，通知/邮件各至多一条/周期；
   - 同一周期重复撤销幂等；
   - 并发撤销/重新验证的最终状态一致。
5. 不要改变设备指纹/公钥语义；不要允许跨账号复用公钥。

### D. P1-06 注销媒体生命周期与头像竞争

#### D1. 删除内容时的附图

1. `AccountDeletionService` 选择“删除内容”时：
   - 软删除帖子/评论；
   - 关联 `PostImage` / `MediaUpload` 标记 `deleted`（或建立可审计的保留策略），并加入对象删除队列；
   - 不要物理删除审计/举报/审核记录。
2. `MediaCleanupService` 能选中这些媒体并删除对象；失败保留记录、可重试、可观测。
3. 普通删帖（作者/管理员）是否也清理媒体？按 SRS F-009/F-036 与现有产品策略决定；至少“注销删除内容”必须闭环。若选择保留，必须有保留期限、理由、审计和访问控制。
4. 测试：
   - 上传 → 发帖绑定 → 注销删除内容 → 清理：帖子不可见、媒体 URL 404/403、对象删除、无永久 attached 残留；
   - 清理失败：记录保留、重试成功；
   - 重复清理幂等。

#### D2. 头像上传与注销竞争

1. 当前 `UploadAvatarAsync` 在保存对象后重新查询用户，但不检查 `DeletedAt/Status`；注销可与之竞争。
2. 修复方式：
   - 事务内锁定用户行/条件更新（`WHERE id=@id AND deleted_at IS NULL`），或在用户上加并发令牌；
   - 上传落库前再次校验账号状态；失败时补偿删除已保存对象；
   - 注销时清理头像对象，并保证之后不能被写回。
3. 测试：
   - 用阻塞 fake storage 精确控制“上传已保存对象 → 注销完成 → 上传继续”，断言注销后 `AvatarUrl` 为空/不被写回，且无孤儿对象；
   - 两个并发头像替换不留下无引用对象；
   - 正常头像上传/清除仍工作。

## 停止条件

- 需要决定“删除内容是否也删除图片”之外的保留期限/法务策略：先问。
- 需要大改用户表/处罚模型/外部 OAuth 依赖：先说明方案与迁移影响。
- 设备通知去重键变更会影响历史通知：说明兼容策略。
- 任何修复需要放宽账号状态检查：拒绝并问。

## 输出

按 `00-master.md` 的阶段结果格式；更新 `fix-log` 中 P1-03～P1-06。建议 commit message 示例：`fix(account): 修复 OAuth 引导、申诉与设备撤销、注销媒体生命周期`。不要 commit/push。
