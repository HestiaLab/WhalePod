# 阶段 0：基线、数据库安全与测试库守卫

> 粘贴到新会话前，先粘贴 `00-master.md`。本阶段只做安全基线与 P1-07 的“测试库守卫”部分；不要改产品行为，不要改恢复脚本（留到阶段 4）。

## 目标

1. 建立可信基线：构建、测试、数据库目标、跳过数量。
2. 修复测试库守卫的子串检查（P1-07 前半），确保任何测试在写库前拒绝非可弃目标。
3. 产出后续阶段可用的安全测试命令与交接摘要。

## 必读

- `docs/reports/architecture-code-review-2026-10-28.md`：P1-07、“本轮实际验证结果”、10.2 命令。
- `tests/WhalePod.Tests.Integration/PersistenceDatabaseTests.cs`
- `tests/WhalePod.Tests.Integration/ServiceTestSupport.cs`
- `tests/WhalePod.Tests.Integration/MediaCleanupServiceTests.cs`
- `README.md` 的构建/测试章节。
- 测试项目的 fixture/collection 定义（`ApiHttpTests.cs`、`WebFormTestSupport.cs` 等）。

## 步骤

### 1. 工作区与基线

```bash
git rev-parse HEAD
git status --short
dotnet --info | head -40
```

- 若工作区有未提交改动：停止并询问；不要 stash/pop（共享工作区风险）。
- 记录 HEAD；与审查基线 `8fd6f6c...` 不同则先说明差异。

### 2. 选择/创建可弃数据库

优先使用本机现有 PostgreSQL 18：

```bash
# 检查 55438（本轮审查实例）是否可用；没有就改用你确认可弃的实例
"C:/Program Files/PostgreSQL/18/bin/pg_isready.exe" -h 127.0.0.1 -p 55438
```

如果 55438 不存在，初始化新的临时集群（示例）：

```bash
mkdir -p artifacts/fix-20261028
"C:/Program Files/PostgreSQL/18/bin/initdb.exe" \
  -D artifacts/fix-20261028/pgdata -U review --auth=trust --encoding=UTF8 --no-locale
"C:/Program Files/PostgreSQL/18/bin/pg_ctl.exe" \
  -D artifacts/fix-20261028/pgdata \
  -l artifacts/fix-20261028/postgres.log \
  -o "-h 127.0.0.1 -p 55439 -c shared_buffers=32MB -c max_connections=40" -w start
"C:/Program Files/PostgreSQL/18/bin/createdb.exe" -h 127.0.0.1 -p 55439 -U review whalepod_test_fix_20261028
```

**不要**使用 `127.0.0.1:55432`，除非用户明确确认它是可弃库；不要复用/删除 `whalepod_browser_review`、`whalepod_test_review`。

连接后确认目标：

```sql
SELECT current_database(), current_user, inet_server_addr(), inet_server_port();
```

必须满足：回环地址、数据库名 `whalepod_test_*`、测试专用用户（或明确的可弃角色）。

### 3. 基线构建与测试

```bash
dotnet build WhalePod.slnx -c Release
WHALEPOD_TEST_DB_CONNECTION="Host=127.0.0.1;Port=<port>;Database=whalepod_test_fix_20261028;Username=review" \
  dotnet test WhalePod.slnx -c Release --no-build --logger trx --results-directory artifacts/fix-20261028/baseline
```

记录：通过/失败/跳过数量；如果 `DatabaseFact` 被跳过，说明数据库守卫或环境变量有问题，先解决再继续。

### 4. 修复测试库守卫（P1-07 前半）

现状：`DatabaseFactAttribute` 只检查连接串是否包含 `whalepod_test`；密码/Application Name/Host 中出现该子串也能通过，之后测试会执行整表删除。

要求：

1. 用 Npgsql 连接串解析器（`NpgsqlConnectionStringBuilder`）读取实际 `Database`、`Host`、`Port`、`Username`。
2. 只允许：
   - 数据库名匹配 `^whalepod_test_[a-z0-9_]+$`（或明确的测试前缀策略）；
   - Host 为回环地址（`127.0.0.1`、`::1`、`localhost`）；
   - 可选：只允许测试专用用户。
3. 连接后执行 `SELECT current_database()` 二次确认；不要只信连接串。
4. 把守卫提取为可测试的 helper（例如 `TestDatabaseGuard`），供 `DatabaseFactAttribute` 与 fixture 共用。
5. 增加负向测试（纯单元测试即可，不连库）：
   - 密码包含 `whalepod_test` 但 Database 不是测试库 → 拒绝；
   - Host 是远程地址 → 拒绝；
   - Database 为空/系统库 `postgres` → 拒绝；
   - 合法 `whalepod_test_fix_20261028` + 回环 → 通过。
6. 不要在测试里“为了跑起来”放宽守卫；不要改成只看环境变量是否存在。

**不要在本阶段修 `restore-postgres.sh`**；那是阶段 4 的内容。

### 5. 验证

```bash
dotnet build WhalePod.slnx -c Release
WHALEPOD_TEST_DB_CONNECTION="Host=127.0.0.1;Port=<port>;Database=whalepod_test_fix_20261028;Username=review" \
  dotnet test WhalePod.slnx -c Release --no-build --logger trx --results-directory artifacts/fix-20261028/after-guard
```

验收：

- 负向测试全部通过；
- 全量测试 0 failed、0 skipped；
- 记录实际数据库名与测试数量；
- 工作区仅包含本阶段改动。

## 停止条件

- 无法确认数据库是可弃的；
- 基线测试失败且与改动无关；
- 需要改测试业务逻辑才能让守卫通过（说明守卫设计有问题，先问）；
- 工作区有未提交改动。

## 输出

按 `00-master.md` 的“阶段结果”格式输出；更新 `docs/reports/fix-log-2026-10-28.md`，把 P1-07 标为“部分完成：测试库守卫已修复，恢复脚本待阶段 4”。不要 commit/push。
