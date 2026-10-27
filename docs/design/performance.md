# WhalePod（鲸群）性能与容量基线

| 项目 | 内容 |
|---|---|
| 文档版本 | v0.1.0 |
| 编制日期 | 2026-10-27 |
| 测试日期 | 2026-10-25 至 2026-10-26 |
| 环境 | Windows 11；.NET 10；PostgreSQL 18.6（本地开发实例 `127.0.0.1:55432`，`shared_buffers=32MB`、`max_connections=30`）；k6 2.2.0 |
| 结论范围 | **仅本地开发实例的缩短时长压测，不构成生产容量承诺**；未连接预发/生产，未启用 CDN/WAF |

## 1. 复制计数端点

被测端点：`POST /internal/counters/copy`（同源校验 + 10 分钟去重 + 原子计数）。脚本：[tests/load/copy-counter-tiers.js](../../tests/load/copy-counter-tiers.js)。

| 档位 | 请求数 | 实际速率 | p50 | p95 | p99 | HTTP 失败 | 计数增量 | 结论 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| baseline_50 | 3,001 | 50.0 rps | 9.11 ms | 11.95 ms | 15.76 ms | 0% | +1 | 通过（同 IP + 产物同桶去重） |
| target_100 | 6,000 | 100.0 rps | 9.27 ms | 12.31 ms | 15.31 ms | 0% | 0 | 通过（同一 10 分钟桶内去重） |
| stress_200 | 12,001 | 200.0 rps | 7.94 ms | 10.30 ms | 13.49 ms | 0% | 0 | 通过（同桶去重） |
| burst_500 | 3,211 | 79.2 rps | 56.48 ms | 19,197.95 ms | 24,840.80 ms | 0% | 0 | **未达到 500 rps**；k6 丢弃 6,789 次迭代，受本地连接上限/连接池影响 |
| duplicate_heavy | 6,001 | 100.0 rps | 8.80 ms | 11.36 ms | 13.99 ms | 0% | +10 | 通过（10 个来源 IP 精确 +10） |
| distinct_heavy | 6,000 | 100.0 rps | 8.24 ms | 11.07 ms | 12.86 ms | 0% | +1,901 | 通过（2,000 个合成 IP 的理论唯一键约 1,901） |

- 时长：`baseline_50`/`target_100`/`stress_200`/`duplicate_heavy`/`distinct_heavy` 为 60 秒，`burst_500` 为 20 秒。
- 计数准确性：`duplicate_heavy` 复核 `Artifact.CopyCount` 与 `Post.CopyCount` 同时 +10；`distinct_heavy` 同时 +1,901；计数非负。
- 初次执行发现全局 Web 写限流与显式 `copy-counter` 策略叠加，60 次后出现大量 429；已改为“有显式限流的端点跳过全局限流”，上表为修复后结果。

## 2. 公开读端点与 SSR

脚本：[tests/load/public-read.js](../../tests/load/public-read.js)。API 与 SSR 各 30 秒。

| 场景 | 目标 | 请求数 | 实际速率 | p50 | p95 | p99 | 失败 | 结论 |
|---|---|---:|---:|---:|---:|---:|---:|---|
| 信息流 | API `/api/v1/feed?sort=latest&limit=20` | 1,501 | 50.0 rps | 6.86 ms | 8.72 ms | 10.64 ms | 0% | 通过 |
| 详情 | API `/api/v1/posts/{id}` | 1,501 | 50.0 rps | 8.14 ms | 10.10 ms | 11.63 ms | 0% | 通过 |
| 搜索 | API `/api/v1/search?q=测试&type=posts` | 901 | 30.0 rps | 11.80 ms | 15.34 ms | 19.21 ms | 0% | 通过 |
| 信息流 | Web `/` | 601 | 20.0 rps | 9.37 ms | 16.50 ms | 261.61 ms | 0% | 通过 |
| 详情 | Web `/posts/{id}` | 600 | 20.0 rps | 12.91 ms | 16.30 ms | 18.39 ms | 0% | 通过 |
| 搜索 | Web `/search?q=测试`（临时提高限流） | 295 | 9.83 rps | 14.11 ms | 123.74 ms | 1,749.85 ms | 0% | 通过；p99 受首请求/渲染抖动影响 |

- 默认 `PublicSearch:RateLimitPerMinute=30` 下，SSR 搜索 10 rps × 30 秒共 300 请求：30 次 200、270 次 429，限流生效。上表搜索延迟是临时放宽限流后的测温结果。
- 组合压测期间曾出现 1 次 API 500：PostgreSQL `53300: sorry, too many clients already`（本地 `max_connections=30`，Web/API/k6 共用）。单跑信息流 50 rps 复测 0 失败，归因于本地连接上限，不是生产容量结论。
- 未对 SSR 首页/详情做 100 rps 以上测试，未启用真实 CDN/WAF。

## 3. 图片上传

脚本：[tests/load/upload-images.js](../../tests/load/upload-images.js)。32×32 PNG，真实登录 + CSRF，API 限流临时放宽。

| 场景 | 请求数 | 速率 | p50 | p95 | p99 | 失败 | 结论 |
|---|---:|---:|---:|---:|---:|---:|---|
| `POST /api/v1/uploads/images` | 41 次上传（另 2 次 setup） | 2.0 rps | 10.31 ms | 13.39 ms | 15.17 ms | 0% | 通过（本地 Local 存储、重编码 WebP） |

生产默认上传限流为 20 次/60 秒/用户；本测试为测处理延迟临时放宽，不表示生产允许 2 rps。

## 4. 中文搜索基线

环境：PostgreSQL 18 本地测试库；3,599 条 approved 帖子、6,432 个用户、971 个标签；`EXPLAIN (ANALYZE, BUFFERS)`，缓存预热。

| 关键词 | 特征 | 执行时间（3 次） | 计划 |
|---|---|---:|---|
| 性能基线 | 命中约 2,000 条 | 4.8 / 4.7 / 6.0 ms | `Index Scan ix_post_feed_active` + Filter |
| 中文两字词 | 命中约 2,012 条 | 3.9 / 3.9 / 3.9 ms | 同上 |
| 编号 1999 | 合成数据中唯一命中 | 8.2 / 5.9 / 6.6 ms | 扫描 3,598 行后保留 1 行 |
| 零命中 | 无匹配 | 5.2 / 5.3 / 5.6 ms | 扫描 3,599 行 |
| 英文宽匹配 | bench | 4.0 / 4.0 / 3.9 ms | 同上 |

结论：当前规模下中文宽匹配与唯一命中均 <10 ms，远低于 3 秒超时；两字中文词无法形成完整 trigram，不能依赖 trigram 索引做高选择性检索。若数据量增长或 p95 接近 1 秒，应改为 `UNION` 分段（帖子字段/作者/标签）或引入异步搜索投影/外部索引，不应把 `ILIKE` 全表扫描直接带到更大数据量。

## 5. 容量结论与后续

- 本地缩短时长结果证明复制计数在 50/100/200 rps 下稳定、读端点 20–50 rps 稳定；`burst_500` 未达标，不能据此宣称 500 rps 容量。
- 本地 `max_connections=30` 是开发恢复参数，不代表生产参数或容量；生产需托管 PostgreSQL、连接池与独立压测环境。
- 未执行：六档 × 10 分钟、持续 `burst_500`、生产/预发压测、PG 连接/锁/WAL 监控、CDN/WAF 指标、设备 challenge/complete 与 Agent 高并发。
- 上线前建议在预发重跑脚本，采集 `pg_stat_activity`、`pg_locks`、WAL、连接池等待、API/SSR 延迟与错误率；在此之前不宣称容量达标。
