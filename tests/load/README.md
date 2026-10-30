# 负载脚本（k6）

本目录脚本用于**手动**容量验证，不在 CI 中执行。默认时长为 10 分钟，可用 `DURATION` 覆盖；本地缩短时长结果不代表容量通过。

## 脚本

| 脚本 | 作用 | 必需环境变量 |
|---|---|---|
| `public-read.js` | 信息流/详情/搜索（API 或 Web SSR） | `BASE_URL`；`MODE=api\|web`、`SCENARIO=feed\|detail\|search`；detail 需 `POST_ID` |
| `copy-counter.js` | 复制计数单档 100 rps | `BASE_URL`、`ORIGIN`、`ARTIFACT_ID` |
| `copy-counter-tiers.js` | 复制计数多档（baseline/target/stress/burst/去重） | `BASE_URL`、`ORIGIN`、`ARTIFACT_ID`、`TIER` |
| `upload-images.js` | 图片上传 | `API_BASE`、`WEB_BASE`、`AUTH_COOKIE`、`CSRF_TOKEN` |

## 正式身份注入

不要依赖 `/dev/test-login`。在预发环境用真实账号完成登录与 CSRF 获取，把 Cookie 与 token 放入秘密存储，再注入：

```bash
export AUTH_COOKIE='whalepod_session=...; whalepod_csrf=...'
export CSRF_TOKEN='...'
```

## 阈值与结果

- 所有脚本包含 `http_req_failed`、`p95/p99`、`checks` 与 `dropped_iterations` 阈值；失败时 k6 退出码非零。
- 10 分钟门禁示例：

```bash
export BASE_URL=https://<site> DURATION=10m
k6 run --summary-export reports/public-read.json tests/load/public-read.js
```

- 报告模板（写入验收记录时）：

```text
提交: <sha>  镜像: <digest>  时间: <start-end>
环境: <DB 规格/连接池/CDN/WAF/实例数>
场景: public-read feed API 50 rps × 10m
结果: p95=<ms> p99=<ms> 失败率=<%> checks=<%> 丢弃迭代=<count>
结论: PASS/FAIL（阈值来自 docs/design/performance.md 与 SRS）
```

## 未执行项

真实 10 分钟三场景、预发 100+ rps SSR、CDN/WAF 与数据库连接上限下的容量结论均未执行；现有本地缩短结果见 [docs/design/performance.md](../../docs/design/performance.md)。
