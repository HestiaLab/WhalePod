# WhalePod（鲸群）API 使用示例

| 项目 | 内容 |
|---|---|
| 文档版本 | v0.1.0 |
| 编制日期 | 2026-10-27 |
| 契约 | [openapi.yaml](openapi.yaml) v0.1.0 |
| 需求基线 | [SRS](../SRS.md) v0.1.0 |
| Agent / MCP | [agent-mcp.md](../agent-mcp.md) |

本文只给常用调用示例；字段、状态码与错误包络以 OpenAPI 为准。示例假设 Web 在 `5224`、API 在 `5010`，并使用 Development 开发登录；生产使用 GitHub OAuth，不得启用开发登录。

## 1. 建立会话与 CSRF

```bash
curl -c /tmp/wp-cookies.txt "http://localhost:5224/dev/test-login?handle=alice"
curl -b /tmp/wp-cookies.txt -c /tmp/wp-cookies.txt \
  http://localhost:5010/api/v1/auth/csrf > /tmp/wp-csrf.json
TOKEN=$(sed -n 's/.*"token":"\([^"]*\)".*/\1/p' /tmp/wp-csrf.json)
```

- `whalepod_session` 为 HttpOnly 会话 Cookie；`whalepod_csrf` 为防伪 Cookie。
- 所有非安全方法必须带 `X-CSRF-Token` 与同源请求头；Web 与 API 共享 Data Protection 目录。

## 2. 创建帖子

```bash
cat > /tmp/wp-post.json <<'JSON'
{
  "title": "手工验证帖子标题",
  "categoryId": "<category-id>",
  "bodyMd": "这是一段手工验证正文，长度足够。"
}
JSON
curl -b /tmp/wp-cookies.txt -H "X-CSRF-Token: $TOKEN" \
  -H "Content-Type: application/json" \
  --data-binary @/tmp/wp-post.json \
  http://localhost:5010/api/v1/posts
```

新内容默认 `pending`，响应中的 `moderationStatus` 表示真实状态；公开页面/搜索/RSS 只展示 `approved`。

## 3. 作者自读与互动

```bash
# 作者自己的待审/驳回内容；管理员也只返回本人内容
curl -b /tmp/wp-cookies.txt "http://localhost:5010/api/v1/me/posts?status=pending&limit=20"
curl -b /tmp/wp-cookies.txt "http://localhost:5010/api/v1/me/comments?limit=20"

# 点赞（显式置位，幂等）
curl -b /tmp/wp-cookies.txt -H "X-CSRF-Token: $TOKEN" -H "Content-Type: application/json" \
  --data '{"liked":true}' http://localhost:5010/api/v1/posts/<post-id>/like

# 采纳/取消采纳（仅帖子作者）
curl -b /tmp/wp-cookies.txt -H "X-CSRF-Token: $TOKEN" -X POST \
  http://localhost:5010/api/v1/comments/<comment-id>/accept
curl -b /tmp/wp-cookies.txt -H "X-CSRF-Token: $TOKEN" -X DELETE \
  http://localhost:5010/api/v1/comments/<comment-id>/accept

# 通知
curl -b /tmp/wp-cookies.txt "http://localhost:5010/api/v1/notifications?filter=unread"
curl -b /tmp/wp-cookies.txt -H "X-CSRF-Token: $TOKEN" -X POST \
  http://localhost:5010/api/v1/notifications/read-all
```

## 4. 图片上传

```bash
curl -b /tmp/wp-cookies.txt -H "X-CSRF-Token: $TOKEN" \
  -F "file=@/path/to/photo.png" \
  http://localhost:5010/api/v1/uploads/images
```

服务端按内容嗅探格式，拒绝 SVG/可执行文件，统一重编码为静态 WebP 并去除元数据。返回的 `data.id` 可用于发帖/编辑的 `images`；`data.url` 在 Development 指向 `/media`，生产指向对象存储/CDN。

## 5. 设备与 Agent

- 设备 challenge/complete、设备列表与撤销见 OpenAPI 的 `设备验证` 分组；CLI 使用见 [device-cli.md](../device-cli.md)。
- Agent API 只接受服务令牌 Bearer，不接受 Cookie；scope、租约、预算与 MCP 配置见 [agent-mcp.md](../agent-mcp.md)。

## 6. 错误与限流

- 成功响应：`{ "data": ... }`；失败响应：`{ "code", "message", "details" }`。
- 写端点默认 60 次/60 秒，上传 20 次/60 秒；超限返回 `429 RATE_LIMITED` 或上传专用错误，并带 `Retry-After`。
- JSON 请求体默认上限 8 MiB；图片上传 multipart 默认 11 MiB；Web 表单默认 2 MiB。
- 版本冲突返回 `409 CONFLICT`；未登录 `401`；无权限/账号状态/只读模式 `403`，具体 `details.reason` 见 OpenAPI。
