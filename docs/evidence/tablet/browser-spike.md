# 平板分发技术探针报告（第一期 · 第一步）

日期：2026-10-21。范围：`docs/plans/2026-10-21-tablet-distribution.md` 第 3 节与第 6 节「第一步：技术探针」。
材料：全部使用合成图片、合成角色名与合成卡片名；未读取、未打印、未提交 `stories/`、`.appdata/`、`.local/` 下任何文件。

**状态总览**：自动化部分已完成并通过；真实平板浏览器部分**全部未执行**（本轮未连接真实 iPad 或安卓平板）。

## 1. 环境事实（实测）

| 项 | 实测值 |
|---|---|
| .NET SDK | 10.0.402（`dotnet --version`），与 `global.json` 锁定一致 |
| 共享框架 | Microsoft.AspNetCore.App 10.0.13 已安装 |
| ASP.NET Core 来源 | `C:\Program Files\dotnet\packs\Microsoft.AspNetCore.App.Ref\10.0.13\ref\net10.0`，含 Components/Server/Kestrel/JSInterop |
| NuGet 新增依赖 | 0（Blazor Interactive Server 随共享框架提供，不引包） |
| 二维码库 | 本机 NuGet 缓存无 QRCoder / Net.Codecrete.QrCodeGenerator / ZXing 等，**离线不可用**，待实施时联网锁定或自行编码 |

## 2. 自动化探针结果（已执行，全部通过）

| 探针 | 用例 | 结果 |
|---|---|---|
| 同进程启停服务 + HTTP 图片 | `TabletServiceSpikeTests`（4 项） | 通过。同进程启动/停止、按 ID 取图返回对应字节、目录只给公开字段、未知 ID 与路径穿越 404、6 个会话各自取到本人角色页 |
| 6 会话并发与刷新 | `TabletConcurrencySpikeTests`（3 项） | 通过。6 个并发请求各取到对应图片；重复 5 次请求字节稳定（等价刷新后重建）；停止服务后所有请求立即失败 |
| Blazor Interactive Server | `TabletBlazorSpikeTests`（1 项） | 通过。真实启动服务（publish 产物子进程），首页外壳与 `_framework/blazor.web.js` 可达（SignalR circuit 的前提），目录接口返回全部名称含两张同名「书房」，按 cardId 取图两张内容不同（同名不串图），未知 ID 与路径穿越 404 |
| Windows 原生 PDF | `WindowsPdfSpikeTests`（3 项） | 部分通过、路线未接入。已实测现有 `CountPdfPages` 字符串计数在对象流压缩的 3 页 PDF 上只数出 1（证实方案「不能以字符串计数作最终依据」的判断）；`Windows.Data.Pdf` 投影尚未接入本工程，用例如实断言不可用并记录原因，未伪造页数 |

构建与测试（`LocalDm.slnx` 已加入 `src/LocalDm.Tablet`）：

```
dotnet build LocalDm.slnx          → 0 警告 0 错误
dotnet test tests/LocalDm.Tests    → 304 总计 / 298 通过 / 6 跳过（EdgeTtsTests 已授权出声跳过）/ 0 失败
dotnet test tests/LocalDm.UiTests  → 33 通过 / 0 失败
```

探针代码位置：`src/LocalDm.Tablet/`（合成最小服务，不是产品实现）、
`tests/LocalDm.Tests/TabletServiceSpikeTests.cs`、`TabletConcurrencySpikeTests.cs`、`TabletBlazorSpikeTests.cs`、`TabletBlazorHost.cs`、`WindowsPdfSpikeTests.cs`。

## 3. 真实设备试验（未执行）

本轮未连接真实 iPad 或安卓平板，下表全部标记「未执行」。按用户 2026-10-21 决定，**不限定 Safari**：iPad 与安卓平板各选一台，用设备上可用的浏览器实测；某款浏览器不兼容时在同一设备上换一款再测。桌面浏览器与模拟器不计入验收。

| # | 设备型号 / 系统 / 浏览器版本 | 动作 | 通过条件 | 实际结果 |
|---|---|---|---|---|
| 1 | 未执行 | 扫码加入局域网页面 | 能选角色，能打开正确图片，无本机证书安装步骤 | 未执行 |
| 2 | 未执行 | 按名称点击单张线索卡 | 点击者平板显示对应卡面；同名卡不串图 | 未执行 |
| 3 | 未执行 | 翻阅整本角色本 + 双指缩放 | 可放大阅读，不禁用浏览器原生缩放 | 未执行 |
| 4 | 未执行 | 横竖屏切换 | 布局可操作，不丢失当前页面 | 未执行 |
| 5 | 未执行 | 6 个浏览器会话并发 | 各自取得对应图片，页码不串台，电脑仍可操作 | 未执行（桌面自动化已覆盖服务端并发，不含真实设备） |
| 6 | 未执行 | 锁屏 60 秒后回来 | 显示断线状态；可恢复或提供明确刷新入口，刷新后回到本局和本人角色 | 未执行 |
| 7 | 未执行 | 切后台 60 秒后回来 | 同上 | 未执行 |
| 8 | 未执行 | Wi-Fi 断开 30 秒后回来 | 同上，且不自动重发电脑操作 | 未执行 |
| 9 | 未执行 | 停止服务 / 切换剧本 / 重启 | 旧页面不再读取旧局资产；重新加入正确局；旧令牌不能取新局同号卡 | 未执行（服务端停止行为已自动化覆盖） |
| 10 | 未执行 | 发布物中的服务运行 | 发布目录内启动可用，无开发 SDK 依赖 | 未执行 |

## 4. 结论与下一步

- **Blazor Interactive Server 路线在本机可行**：零新增 NuGet 依赖、离线可构建，SignalR 所需脚本资源可达。技术候选维持 Blazor Server，待真实平板验证后定型。
- **二维码生成依赖未定**：本机离线不可用，需在实施第三步前锁定库与版本（或自行实现 QR 编码），并核查许可。
- **PDF 原生渲染路线未接入**：现有字符串页数计数已被实测证明在压缩 PDF 上不可靠；`Windows.Data.Pdf` 投影接入属于第二步资产准备工作，届时必须实际跑通页数与逐页渲染并核对发布物依赖。
- **技术门槛未通过**：真实平板浏览器验收（第 3 节表）尚未执行。按方案要求，**在它通过前不进入第二步的正式建设**。
