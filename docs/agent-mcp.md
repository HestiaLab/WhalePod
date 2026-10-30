# 本机 AI 审核 Agent 与 stdio MCP 接入指南

| 项目 | 内容 |
|---|---|
| 文档版本 | v0.1.0 |
| 协议 | Agent API：OpenAPI 3.1 `/agent/*`；MCP：stdio JSON-RPC |
| MCP 协议版本 | `2025-06-18` |
| 工具 Schema 版本 | `1` |
| 默认状态 | `ai.enabled=false`；关闭时 `/agent/*` 返回 `AGENT_DISABLED`，MCP Server 不启动 |
| 交付状态 | Agent API、stdio MCP、预算/熔断与人工裁决边界已实现；Q-02 模型选型与真实 MCP 客户端 + 模型 E2E 待外部环境 |

AI 只提交结构化建议，不改变内容审核状态；所有内容必须人工裁决。当前版本不提供自动通过/驳回、账号处罚、策略修改或公网 MCP 端点。

## 1. 管理员准备

1. 在 `/admin/service-accounts` 创建服务账号，scope 只能选择：
   - `moderation:read`：任务列表/详情/策略；
   - `moderation:suggest`：领取/释放/提交建议；
   - `agent:heartbeat`：心跳。
2. 可选配置精确 IP allowlist（动态 IP 下保持为空；生产入口在可信 WAF/反向代理后仍执行令牌与 scope 校验）。
3. 签发令牌；明文只在创建/轮换页面显示一次。服务端只保存 SHA-256 哈希，并记录最后使用时间。
4. 在 `/admin/ai` 配置至少一个非占位批准模型、每日 token 预算，并启用 AI/MCP；启用需确认已完成 Q-02 模型选型与质量评估。

## 2. Agent API

所有 `/api/v1/agent/*` 只接受 `Authorization: Bearer <service-token>`，不接受 Cookie；响应 `Cache-Control: no-store`。除心跳与策略外，必须携带心跳返回的 `X-Agent-Instance-Id`。

| 端点 | Scope | 说明 |
|---|---|---|
| `GET /api/v1/agent/moderation/tasks` | `moderation:read` | 仅 queued 与当前实例 claimed 任务；游标分页 |
| `GET /api/v1/agent/moderation/tasks/{id}` | `moderation:read` | 受控内容快照；`untrustedContent=true` |
| `POST /api/v1/agent/moderation/tasks/{id}/claim` | `moderation:suggest` | 原子领取，默认 10 分钟租约 |
| `POST /api/v1/agent/moderation/tasks/{id}/release` | `moderation:suggest` | 主动释放；建议暂停时仍可归还租约 |
| `POST /api/v1/agent/moderation/tasks/{id}/suggestion` | `moderation:suggest` | 只写建议，内容仍 pending |
| `POST /api/v1/agent/heartbeat` | `agent:heartbeat` | 注册/更新实例；返回 instanceId 与 `suggestionsPaused` |
| `GET /api/v1/agent/moderation/policy` | `moderation:read` | 策略版本、类别、租约、批准模型与内容限制 |

**门禁**

- `ai.enabled=false`：所有 `/agent/*` 返回 `503 AGENT_DISABLED`。
- `ai.suggestions_paused=true`：heartbeat/policy/列表/详情/release 可用；claim/suggestion 返回 `503 AGENT_DISABLED`；不生成新任务。
- 模型或运行位置不在批准列表/非 local：心跳拒绝，已有实例标记 paused 并审计。
- 连续非法输出/超时由 Agent 自身退避；服务端租约到期自动回到队列，不自动通过。
- 任务内容按 PII/疑似密钥规则脱敏并截断；`ContentHash` 与 `contentVersion` 变化会使旧任务失效。

## 3. 本机 Agent / MCP 配置

Agent 进程只发起出站 HTTPS；MCP 仅 stdio，不监听 HTTP/WebSocket。令牌放在本机环境变量或操作系统秘密存储中，不写入仓库、不打印、不返回给 MCP 客户端。

默认 API 地址为 `https://localhost:5010`。本机开发如需回环 HTTP，必须显式设置 `WHALEPOD_AGENT_ALLOW_INSECURE_HTTP=true`；非回环 HTTP 一律拒绝。

```bash
export WHALEPOD_AGENT_API_URL="https://whalepod.example.com"
# 本机开发：export WHALEPOD_AGENT_API_URL="http://localhost:5010"
# 本机开发：export WHALEPOD_AGENT_ALLOW_INSECURE_HTTP="true"
export WHALEPOD_AGENT_TOKEN="<service-token>"
export WHALEPOD_AGENT_MODEL_NAME="deepseek-flash"
export WHALEPOD_AGENT_MODEL_VERSION="<approved-version>"
export WHALEPOD_AGENT_MODEL_QUANTIZATION="<approved-quantization>"
# 可选：
# WHALEPOD_AGENT_INSTANCE_NAME / WHALEPOD_AGENT_HOST_FINGERPRINT
# WHALEPOD_AGENT_PROMPT_VERSION

dotnet run --project src/WhalePod.Agent
```

启动时 Agent 先发送心跳并读取策略；若 AI/MCP 关闭，进程以 0 退出且不启动 MCP Server；若令牌/模型配置错误则非零退出。MCP 客户端通过 stdio 启动该进程，JSON-RPC 写 stdout，诊断日志只写 stderr。客户端断开或进程退出时，Agent 尽力释放已领取租约，未释放的租约到期自动回收。

工具清单（只读/工作流工具，无 shell、文件系统、任意 HTTP 或管理工具）：

- `moderation_list_tasks`
- `moderation_get_task`
- `moderation_claim_task`
- `moderation_submit_suggestion`
- `moderation_release_task`
- `moderation_heartbeat`
- `moderation_get_policy`

工具 Schema 带版本号；任务内容输出标记 `untrusted_content=true`，内容里的“忽略上述指令/自动通过”等文本不得改变工具行为。工具错误返回结构化 code/message，不包含令牌或 PII。

## 4. 质量与暂停

- 管理端审核队列显示当前版本的真实 AI 建议、置信度、策略码、理由与模型版本；管理员可通过/驳回/隐藏/升级。采纳建议可一键确认，覆盖建议必须填写原因。
- `/admin/ai` 展示真实建议总数、待处理、人工已裁决、采纳/覆盖与人工一致率，以及按建议类型/模型分组统计；不展示模型隐藏推理或用户正文。
- 暂停/恢复与启用/关闭写审计；暂停不会让 Agent 失联，心跳与策略读取继续可用。
- 启用前仍需完成 Q-02 模型选型与不少于 200 条标注样本的质量评估；本仓库当前未内置本地模型，也未执行该评估。

## 5. 已知限制

- 未实现 AI 自动通过/驳回（当前版本明确不提供）；AI 不得禁言、封禁、删除账号或改策略。
- 任务由 Agent 轮询列表时懒同步生成，单次最多处理 50 条待审帖子与 50 条评论；大规模积压需分批轮询或后续后台任务化。
- 服务端已实现基于过期租约与非法建议的失败熔断，并在 `/admin/ai`、`/admin/system` 展示；尚未实现按模型质量下降自动触发的新阈值策略与独立告警面板。
- MCP 的 live 端到端（真实本地模型 + 生产 API）尚未执行；已覆盖工具 Schema/参数校验/stdio 分发与 Agent API HTTP 流程。
- Agent 不替代人工审核；内容在人工处理前保持 `pending`。
