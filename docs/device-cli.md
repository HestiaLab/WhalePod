# WhalePod Device CLI

| 项目 | 内容 |
|---|---|
| 文档版本 | v0.1.0 |
| CLI 版本 | 0.1.0 |
| 协议 | `community-device-v1`（协议版本 1） |
| 发布日期 | 2026-10-26 |
| 支持平台 | `win-x64`、`linux-x64`、`osx-arm64`；自包含单文件，无需预装 .NET |
| 实机验收 | Windows 已端到端验证；Linux/macOS 发布产物已生成，待在对应实机跑通验证流程 |

设备验证只证明“提交者持有对应 Ed25519 私钥”，不证明运行环境未被篡改。CLI 不联网、不自动更新、不读取站点令牌；私钥只保存在本机，永不上传、打印或写入日志。

## 获取与校验

从 GitHub Releases 下载对应平台的发布包，并校验 `SHA256SUMS`：

```bash
# Linux / macOS
sha256sum -c SHA256SUMS
# macOS 若无 sha256sum
shasum -a 256 -c SHA256SUMS
```

```powershell
# Windows PowerShell
Get-FileHash .\whalepod-device.exe -Algorithm SHA256
```

- Windows 可能触发 SmartScreen；请先核对来源与校验和。
- macOS 未公证版本需要按发布说明在“隐私与安全性”中手动允许。
- Linux 需要 `chmod +x whalepod-device`。

## 使用

```bash
whalepod-device version
whalepod-device sign --challenge <base64url> --handle <handle>
```

1. 在站点 `/settings/devices` 生成一次性挑战，复制页面给出的命令。
2. 在本机运行命令；首次运行会在用户目录生成本地 Ed25519 密钥。
3. 把 CLI 输出的 JSON 粘贴回页面提交。CLI 输出包含公钥、签名、challenge、handle、时间戳、内核与平台，不包含私钥。

默认密钥文件：

- Windows：`%APPDATA%\WhalePod\device-key.json`
- macOS：`~/Library/Application Support/WhalePod/device-key.json`
- Linux：`$XDG_CONFIG_HOME/WhalePod/device-key.json`（通常 `~/.config/WhalePod/device-key.json`）

可用 `--key-file <path>` 或环境变量 `WHALEPOD_DEVICE_KEY_FILE` 覆盖。Unix 下密钥目录设为 `700`、文件设为 `600`；Windows 依赖用户配置目录的 ACL，建议启用磁盘加密。请备份密钥文件；私钥丢失无法恢复，只能撤销旧设备后重新验证。

## 退出码

| 退出码 | 含义 |
|---:|---|
| 0 | 成功 |
| 2 | 参数错误 |
| 3 | 平台不支持 |
| 4 | 密钥文件不可用 |
| 5 | 签名失败（含无法获取内核版本） |
| 1 | 其他错误 |

## 本地发布与 SHA-256 清单

```powershell
# Windows
pwsh scripts/publish-device-cli.ps1 -Version 0.1.0
```

```bash
# Linux / macOS / Git Bash
./scripts/publish-device-cli.sh 0.1.0
```

脚本会发布三个 RID 到 `artifacts/device-cli/<version>/`，并生成 `SHA256SUMS`。CI 的 GitHub Release 工作流会对上传的 zip 包生成同名清单；不要把本地生成的二进制或私钥提交到仓库。
