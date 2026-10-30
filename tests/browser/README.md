# 本地浏览器 smoke / axe 脚本

无 npm 依赖，使用 Node 22+ 内置 `fetch`/`WebSocket` 通过 CDP 驱动本机 Chrome/Edge。脚本**不进入 CI**（CI 无图形环境与浏览器），作为 UI 变更后的本地验收与证据产出工具。

## 用法

```bash
# 先启动 Web（Development、测试库、本地媒体）
export BASE_URL=http://localhost:5224
node tests/browser/smoke.mjs

# 带 axe-core（默认从 jsDelivr 拉取）
AXE=1 node tests/browser/smoke.mjs

# 指定页面与浏览器
PAGES='/,/search?q=test,/login' CHROME_PATH='C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe' \
  node tests/browser/smoke.mjs
```

- 视口：1280×900 与 375×812。
- 产出：`tests/browser/reports/<timestamp>/` 下的截图、`summary.json`（标题、scrollWidth、控制台错误、axe 违规）与 axe 明细。
- 失败条件：横向溢出、控制台错误、axe serious/critical 违规；退出码 1。
- 需要登录的页面请先自行完成登录（脚本不保存凭据）；默认只覆盖公开页。

历史 axe 结论的原始报告未留存；后续 UI 变更请把 `reports/` 中的摘要与命令写入验收记录（不要提交包含个人数据的截图）。
