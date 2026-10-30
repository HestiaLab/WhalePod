# WhalePod 部署与回滚 Runbook

本目录提供容器构建与部署步骤的**仓库侧产物**，不包含任何云厂商账号、密钥、域名或集群清单。生产域名、TLS 证书、SMTP 与对象存储凭据由运维通过秘密存储注入。

## 1. 产物与前置

- `deploy/Dockerfile`：Web/API 运行镜像、迁移工具镜像（`web`/`api`/`migrate` target）。
- `deploy/compose.yaml`：PostgreSQL 18.6、Web、API、迁移与持久卷示例。
- `deploy/.env.example`：必需配置模板；复制为 `.env` 并填入秘密。
- 构建要求：.NET SDK `10.0.401`（`global.json` 固定）、NuGet 锁文件（`packages.lock.json`，构建使用 `--locked-mode`）。
- 生产要求：HTTPS 入口、显式 `AllowedHosts`、`Email:PublicBaseUrl` 为 HTTPS、`Email:Smtp:Security` 为 `StartTls`/`SSL`。

## 2. 构建

```bash
docker build -f deploy/Dockerfile --target web -t whalepod-web:<tag> .
docker build -f deploy/Dockerfile --target api -t whalepod-api:<tag> .
docker build -f deploy/Dockerfile --target migrate -t whalepod-migrate:<tag> .
```

相同提交应得到相同依赖（`--locked-mode`）；发布流水线同时生成 ZIP 的 `SHA256SUMS`。将镜像 digest、提交 SHA 与备份文件哈希记录在同一发布记录中。

## 3. 首次部署

```bash
cp deploy/.env.example deploy/.env   # 填入秘密，勿提交
docker compose -f deploy/compose.yaml --env-file deploy/.env --profile migrate run --rm migrate
docker compose -f deploy/compose.yaml --env-file deploy/.env up -d db web api
curl -fsS http://127.0.0.1:8080/healthz/ready
curl -fsS http://127.0.0.1:8081/healthz/ready
```

`/healthz/ready` 在数据库不可达、存在未应用迁移或关键表探测失败时返回 503。

## 4. 备份

数据库与对象/Data Protection 必须一起备份，否则恢复后 Cookie 或媒体链接会失效。

```bash
export WHALEPOD_DB_CONNECTION='Host=...;Port=5432;Database=whalepod;Username=...;Password=...'
bash scripts/backup-postgres.sh  # 生成 dump + sha256 校验文件
# 同时快照：pgdata 卷、dpkeys 卷、media 卷或对象存储桶
```

## 5. 恢复

恢复到**新数据库**（脚本默认拒绝覆盖非空库；仅显式 `--allow-existing` 才原位覆盖，且强制单事务）：

```bash
bash scripts/restore-postgres.sh <backup.dump> --checksum <backup.dump.sha256> --target-db whalepod_restore
```

恢复完成后先对 `whalepod_restore` 执行 `/healthz/ready` 验证，再切换连接串/流量。媒体与 Data Protection 卷按同一时间点恢复。

## 6. 回滚策略（重要）

**本版本不支持数据库降级。** 迁移的 `Down` 在已有 `appeal_result` 通知或头像等新数据时会失败或丢失数据。回滚策略固定为：

1. 停止写入/切走流量；
2. 部署上一版本镜像（应用回滚）；
3. 如 schema 已前进，按第 5 节从备份恢复到新库并切换连接；
4. 恢复对象存储/DP 密钥快照；
5. 运行 `/healthz/ready` 与冒烟脚本后切回流量。

数据损失窗口 = 最近一次成功备份到故障发生之间。演练记录见 `docs/reports/fix-log-2026-10-28.md` 阶段 7。

## 7. 升级与演练清单

- [ ] 备份数据库、对象存储与 DP 密钥，记录 SHA-256；
- [ ] `--profile migrate run --rm migrate`（Up 前先在预发演练）；
- [ ] 两个服务的 `/healthz/ready` 返回 200；
- [ ] 公开页、登录、发帖、审核、评论冒烟；
- [ ] 回滚演练：上一镜像 + 备份恢复；记录耗时与数据损失窗口。

未在真实云资源上演练的部分（TKE/负载均衡/证书托管）列为外部运维前置，仓库不伪造该证据。
