# US5 安装包与便携包：实测记录

更新日期 2026-10-19。

## 结论

| 场景 | 结论 | 证据 |
|---|---|---|
| 自包含发布（不要求 .NET SDK / 运行时） | **已通过** | `portable-smoke.json`：`selfContained=true`，PATH 里没有 dotnet |
| 便携包启动 | **已通过** | 0.8 秒出窗口，标题 `本局主持`，截图 `portable-start.png` |
| 便携 ZIP 生成 | **已通过** | `artifacts/LocalDm-win-x64.zip`，60.7 MB |
| 安装器生成 | **已通过** | `artifacts/LocalDm-Setup-x64.exe`，44.6 MB（Inno Setup 6.7.3） |
| 静默安装 → 启动 → 卸载 | **已通过** | 安装退出码 0，应用窗口可开，卸载退出码 0，安装目录被清除 |
| 卸载保留用户数据 | **已通过** | 卸载后 `sessions.db` 仍在数据根目录 |
| 数据不写安装目录 | **已通过** | 安装目录内无 `sessions.db` / `settings.json` |
| 代码签名 / SmartScreen | **未签名** | 见限制 |
| 真实 4–6 人整局 | **未执行** | 见限制 |

## 命令

```powershell
# 构建两个发布物
pwsh -NoProfile -File packaging/windows/Publish.ps1

# 无 SDK 环境启动校验
python scripts/verify-portable.py
```

## 发布物

| 文件 | 大小 | 说明 |
|---|---|---|
| `artifacts/LocalDm-Setup-x64.exe` | 44.6 MB | per-user 安装器（无需管理员），快捷方式，卸载不删用户数据 |
| `artifacts/LocalDm-win-x64.zip` | 60.7 MB | 便携目录压缩包 |
| `artifacts/publish/win-x64/` | 253 文件 / 134.5 MB | 自包含发布目录，含 247 个 DLL 与 `hostfxr.dll` |

构建工具版本：.NET SDK 10.0.402，Inno Setup 6.7.3（`ISCC.exe`）。

## 无 SDK 环境启动结果

```json
{
  "selfContained": true,
  "sdkOnPath": false,
  "pathUsed": "C:\\Windows\\System32;C:\\Windows",
  "secondsToWindow": 0.8,
  "windowTitle": "本局主持",
  "dataFilesCreated": ["sessions.db"]
}
```

启动环境刻意只保留 `SystemRoot` / `TEMP` / `LOCALAPPDATA` / 最小 `PATH`，并设置独立 `LOCALDM_DATA_ROOT`，
证明程序不依赖开发机上的 SDK、Node、Python。

## 安装 / 卸载实际结果

```text
install exit=0
installed LocalDm.exe: True
app running: True
uninstall exit=0
install dir removed: True
user data preserved: True
```

## 数据保留策略

- 运行数据固定在 `%LOCALAPPDATA%\LocalDm`（可用 `LOCALDM_DATA_ROOT` 覆盖），**不在安装目录**。
- 卸载时若有未静默的交互，会先明确询问；静默卸载（`/VERYSILENT`）不弹框但仍不删除用户数据。
- 安装器与卸载脚本都**不会**删除 `%LOCALAPPDATA%\LocalDm` 下的剧本包、存档与设置。

## 限制与未验证项

- **未做代码签名**：安装器和 exe 未签名，Windows SmartScreen 可能提示未知发布者。按宪章不要求绕过安全警告，此处如实报告。
- **自包含发布不含系统声音**：System.Speech 依赖目标机器已安装的中文音色（本机有 4 个）。
- **没有在另一台"干净"物理机上安装**：本轮在本机构建与验证，用最小 PATH 模拟无 SDK 环境。
- **没有真实 4–6 人整局**：需要真人玩家到场；且真实剧本因 MinerU 引擎阻塞尚未准备为 Ready 包。
- 未做升级覆盖测试（只有一个版本）。
