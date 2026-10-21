# 平板分发技术探针报告（第一期 · 第一步，修订）

日期：2026-10-21。范围：`docs/plans/2026-10-21-tablet-distribution.md` 第 3 节与第 6 节「第一步：技术探针」。
本文件取代同日早前版本，逐项落实 `docs/reviews/2026-10-21-tablet-spike-review.md` 的五项发现。
材料：全部合成；未读取、未打印、未提交 `stories/`、`.appdata/`、`.local/` 下任何文件。

**状态总览**：五项审查发现已全部修正；自动化部分通过；**桌面真实浏览器**已确认选角色、点卡、翻页可用；**真实平板浏览器仍未执行**（未连接 iPad / 安卓平板）。

## 1. 审查发现逐项处理

| # | 发现 | 处理 | 证据 |
|---|---|---|---|
| P1#1 | 页面未启用交互渲染，按钮不能执行 | `Pages/Home.razor` 显式声明 `@rendermode RenderMode.InteractiveServer`；`App.razor` 改为 `Router` + `RouteView` 根组件 | 真实 Chrome 点击角色按钮后页面切换到目录 |
| P1#2 | 图片写入无效字节，浏览器无法显示 | 改为真实 PNG 编码器（RGB8 + zlib 包装 + Adler-32 校验 + CRC）；根因是只写裸 deflate，WIC 容错而浏览器解成全透明 | 浏览器 canvas 解出的条带颜色与服务端逐像素解码一致；截图可见色带 |
| P1#3 | 全局单例把所有平板的状态混在一起 | `TabletSpikeState` 改为按 Cookie 会话键分桶；`/tablet/role/pages/{n}` 与 `/tablet/claim` 都从 Cookie 解析会话；无 Cookie 的角色页请求直接 404 | 全新 Cookie 容器看到角色 1、2 已被认领且按钮禁用；抢占返回 409；清 Cookie 后角色页 404 |
| P1#4 | 多会话测试验证另写的假服务 | 新增 `TabletAppTests` 直接启动 `src/LocalDm.Tablet` 的 publish 产物，用独立 Cookie 容器断言各自图片内容互不相同（18 张图 18 个不同内容）；`TabletBlazorHost` 改为每次测试独立发布目录、只 publish 一次、完整回收进程 | `TabletAppTests` 4 项通过 |
| P2#5 | 原生 PDF 探针没有实际调用 API | 真实接入 `Windows.Data.Pdf`：`PdfDocument.LoadFromFileAsync` + `PageCount` + `PdfPage.RenderToStreamAsync` + `BitmapDecoder` 取像素；测试工程目标改为 `net10.0-windows10.0.26100.0` 以获得 WinRT 投影 | `WindowsPdfSpikeTests` 5 项通过，含页数、逐页渲染、绘制内容位置 |

## 2. 自动化探针结果（已执行，全部通过）

| 测试文件 | 用例数 | 覆盖 |
|---|---|---|
| `TabletAppTests` | 4 | 真实应用：6 会话各自取图且内容互不相同、认领冲突 409 且先到者不受影响、卡片是真实 PNG 且同名卡不同、未知 ID 与路径穿越 404、无 Cookie 取不到角色页 |
| `TabletBlazorSpikeTests` | 1 | 真实应用：首页外壳、`_framework/blazor.web.js` 可达、目录只给公开名称不夹带图片字节 |
| `TabletServiceSpikeTests` | 4 | 同进程启停、按 ID 取图、目录公开字段、6 会话角色页（标注为传输小实验） |
| `TabletConcurrencySpikeTests` | 3 | 6 并发、重复请求稳定、停止即失效（标注为传输小实验） |
| `TabletSessionIsolationTests` | 1 | Cookie 会话隔离与认领冲突（标注为会话小实验） |
| `WindowsPdfSpikeTests` | 5 | 原生页数、逐页渲染可解码、绘制内容位置、字符串计数漏页边界、畸形对象流样本打不开时如实记录 |

命令与结果（本轮实测）：

```
dotnet build LocalDm.slnx          -> 0 警告 0 错误
dotnet test tests/LocalDm.Tests    -> 312 总计 / 306 通过 / 6 跳过（EdgeTtsTests 已授权出声跳过）/ 0 失败
dotnet test tests/LocalDm.UiTests  -> 33 通过 / 0 失败
```

## 3. 桌面真实浏览器结果（已执行）

浏览器：Chrome 156.0.8078.47（Playwright CLI 驱动的真实浏览器，非模拟器）。
地址：`http://127.0.0.1:5080/`（探针 publish 产物，`--urls http://0.0.0.0:5080`）。

| 动作 | 结果 | 证据 |
|---|---|---|
| 打开页面，出现 6 个合成角色按钮 | 通过 | snapshot 显示 6 个按钮 |
| 点击「合成角色 1」 | 通过 | 页面切换到目录，显示「当前角色：合成角色 1」 |
| 点击卡片「书房」 | 通过 | `img.shot` 300x200，canvas 解出 4 个条带颜色与服务端一致 |
| 点击「第 2 页」 | 通过 | `img` 变为 400x600，`src=/tablet/role/pages/2` |
| 刷新页面 | 通过 | 仍持有角色 1，两张图的 src 保持不变 |
| 换新 Cookie 容器 | 通过 | 看到角色 1、2 已被认领且按钮禁用；认领角色 3 成功 |
| 接口层：抢占已认领角色 | 通过 | 409 |
| 接口层：清 Cookie 后取角色页 | 通过 | 404 |
| 接口层：路径穿越 | 通过 | 404 |

截图：`artifacts/tablet/desktop-a-card.png`、`desktop-a-role-page.png`、`desktop-c-role3.png`；
结构化记录：`artifacts/tablet/desktop-verification.json`。

## 4. 真实设备试验（未执行）

本轮未连接真实 iPad 或安卓平板，下表全部标记「未执行」。按用户 2026-10-21 决定，**不限定 Safari**：
iPad 与安卓平板各选一台，用设备上可用的浏览器实测；某款浏览器不兼容时在同一设备上换一款再测。
桌面浏览器与模拟器不计入验收。

| # | 设备型号 / 系统 / 浏览器版本 | 动作 | 通过条件 | 实际结果 |
|---|---|---|---|---|
| 1 | 未执行 | 扫码加入局域网页面 | 能选角色，能打开正确图片，无本机证书安装步骤 | 未执行 |
| 2 | 未执行 | 按名称点击单张线索卡 | 点击者平板显示对应卡面；同名卡不串图 | 未执行 |
| 3 | 未执行 | 翻阅整本角色本 + 双指缩放 | 可放大阅读，不禁用浏览器原生缩放 | 未执行 |
| 4 | 未执行 | 横竖屏切换 | 布局可操作，不丢失当前页面 | 未执行 |
| 5 | 未执行 | 6 个浏览器会话并发 | 各自取得对应图片，页码不串台，电脑仍可操作 | 未执行（服务端并发已自动化覆盖，真实设备并发未覆盖） |
| 6 | 未执行 | 锁屏 60 秒后回来 | 显示断线状态；可恢复或提供明确刷新入口，刷新后回到本局和本人角色 | 未执行 |
| 7 | 未执行 | 切后台 60 秒后回来 | 同上 | 未执行 |
| 8 | 未执行 | Wi-Fi 断开 30 秒后回来 | 同上，且不自动重发电脑操作 | 未执行 |
| 9 | 未执行 | 停止服务 / 切换剧本 / 重启 | 旧页面不再读取旧局资产；重新加入正确局；旧令牌不能取新局同号卡 | 未执行（服务端停止行为已自动化覆盖） |
| 10 | 未执行 | 发布物中的服务运行 | 发布目录内启动可用，无开发 SDK 依赖 | 未执行 |

## 5. 环境与依赖事实（实测）

| 项 | 实测值 |
|---|---|
| .NET SDK | 10.0.402（与 `global.json` 一致） |
| 共享框架 | Microsoft.AspNetCore.App 10.0.13 |
| ASP.NET Core / Blazor 来源 | SDK targeting pack `C:\Program Files\dotnet\packs\Microsoft.AspNetCore.App.Ref\10.0.13` |
| 新增 NuGet 依赖 | 0 |
| 二维码库 | 本机 NuGet 缓存无任何 QR 相关包，**离线不可用**，待实施第三步前锁定或自行编码 |
| WinRT 投影 | 测试工程改为 `net10.0-windows10.0.26100.0` 后 `Windows.Data.Pdf` 可调用 |

## 6. 结论与下一步

- **Blazor Interactive Server 路线在本机可行**：零新增 NuGet 依赖、离线可构建，真实 Chrome 已确认交互与图片显示。
- **原生 PDF 路线已实际接入**：页数与逐页渲染均通过合成 PDF 验证；现有字符串页数计数的漏页边界已实测记录。
- **二维码生成依赖未定**：本机离线不可用，需在第三步前锁定库与版本（并核查许可）或自行实现 QR 编码。
- **技术门槛未通过**：真实 iPad 与安卓平板验收尚未执行。按方案要求，**在它通过前不进入第二步的正式建设**。
