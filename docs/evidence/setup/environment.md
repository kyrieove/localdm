# T001 构建环境实证

记录日期 2026-10-19。以下为在本机实际执行命令的输出，不包含任何真实剧本内容、密钥或私人路径。

## 环境

| 项 | 实测值 | 取得方式 |
|---|---|---|
| 操作系统 | Microsoft Windows 11 家庭版 中文版 10.0.26300 | `Get-CimInstance Win32_OperatingSystem` |
| 处理器架构 | AMD64（x64） | `$env:PROCESSOR_ARCHITECTURE` |
| .NET SDK | 10.0.402（安装前本机**无 SDK**，只有运行时 8.0.32 / 10.0.13） | `dotnet --list-sdks` |
| .NET 运行时（桌面） | Microsoft.WindowsDesktop.App 10.0.13、8.0.32 | `dotnet --info` |
| 解决方案格式 | `LocalDm.slnx`（SDK 10 默认生成） | `dotnet new sln` |
| MinerU | `MinerU 版本: 4.0.1`、`Python 版本: 3.12.14`，可执行 `C:\Users\ASUS\.local\bin\mineru.exe` | `mineru version` |
| Windows Magpie | 本机 `http://127.0.0.1:3425/v1/models` 返回 HTTP 200（30631 字节） | `Invoke-WebRequest` |
| 中文音色 | Microsoft Huihui Desktop / Microsoft Huihui / Microsoft Kangkang / Microsoft Yaoyao（均 zh-CN） | `SpeechSynthesizer.GetInstalledVoices()` |
| OpenLess | 已安装 2.0.1+build.20261016（`D:\2\software\OpenLess`） | 卸载注册表项 |
| Codex CLI | codex-cli 0.163.0-alpha.10.2 | `codex --version` |

## 本节实际执行的准备动作

1. 本机缺少 .NET SDK，经用户确认后安装：`winget install --id Microsoft.DotNet.SDK.10 --accept-package-agreements --accept-source-agreements`。
   结果：安装版本 10.0.402，`dotnet --list-sdks` 可见。
2. 其余依赖（Magpie、OpenLess、MinerU、中文音色）为本机已有，未安装或修改任何配置。

## 未验证 / 限制

- 只确认 Magpie 列表端点可访问；真实生成、取消与播音仍是 US1/US5 的实机任务。
- 中文音色仅确认已注册，未在本任务中实际播音。
- MinerU 仅确认版本号；服务是否运行、引擎可用性与隐私状态属 US2。
- 本文件不构成本项目任何应用行为的验收结果。

## 相关版本锁定

见 `Directory.Packages.props`（集中版本）与各工程 `packages.lock.json`（还原锁定）。
