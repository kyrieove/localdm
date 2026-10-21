# 平板技术探针审查：e4d46c2

日期：2026-10-21。审查目标固定为提交 `e4d46c2`，不是当前工作区。审查时工作区已有未提交的 Tablet 页面、服务、状态和测试修订及新增文件，本轮未改动这些文件，也未将它们视为已提交修复。

结论：**第一步尚未完成，不宜直接请用户用平板验收这份提交。** 自动化证明了部分 HTTP 传输行为，没有证明实际页面能交互、图片能显示或角色会话隔离。原生 PDF 验证也未完成。

本轮进行了提交级代码和证据核对，以及独立图片解码复现；没有重跑全量构建测试。DeepSeek 报告的 298 条通过、6 条跳过及 33 条 UI 测试通过仍标作其提交报告，不是本轮独立复测结果。未读取受保护的真实材料目录。

## 1. [P1] 页面未启用交互渲染，按钮不能执行

提交位置：`src/LocalDm.Tablet/Pages/Home.razor:1`、`src/LocalDm.Tablet/App.razor:13`。

Home 及父级 Router 都没有声明交互 render mode。Program 中注册 `AddInteractiveServerComponents` 和 `AddInteractiveServerRenderMode` 提供能力，但不自动把页面变为交互页面。当前按默认静态 SSR 渲染，按钮上的 `@onclick` 不会被执行。加载 `blazor.web.js` 成功不能证明页面已经建立交互 circuit。

应显式设置适当的 InteractiveServer 边界，并用真正浏览器验证选角色、点卡、翻页后 DOM 和实际图片发生正确变化。不能只断言脚本和首页 HTTP 200。

依据：[Microsoft 官方 render modes 文档](https://github.com/dotnet/aspnetcore.docs/blob/main/aspnetcore/blazor/components/render-modes.md)，通过 Context7 查询核对。文档明确展示不声明交互模式时按钮不会调用事件处理函数。

## 2. [P1] 图片写入的是无效字节，浏览器无法显示

提交位置：`src/LocalDm.Tablet/Program.cs:121`。

`SyntheticAssets.Write` 只写入 9 字节，连完整的 PNG 签名都没有，更没有 IHDR、像素数据和 IEND。它不能成为浏览器可解码的图片。当前测试只检查非空和不同卡字节不同，因此损坏图片也会通过。

独立复现：使用同样九字节结构构造 MemoryStream，以 Windows System.Drawing.Image.FromStream 解码，结果为 `Parameter is not valid`。该验证没有读取或修改仓库资产。

应生成真实有效、每张有明显不同内容和页码的合成图片，补充解码和尺寸断言；浏览器验证 `naturalWidth`、`naturalHeight` 及图片标记。只有响应 200 或 `Content-Type: image/png` 不足以证明阅读可用。

## 3. [P1] 全局单例把所有平板的角色和阅读状态混在一起

提交位置：`src/LocalDm.Tablet/Program.cs:48`、`src/LocalDm.Tablet/TabletSpikeState.cs`。

`TabletSpikeState` 被注册为 Singleton，其中只有一个 `_roleId`、`SelectedCardId` 和 `RolePage`。任何客户端选择角色都覆盖所有客户端的角色；HTTP 角色页接口也只读取这一个全局角色。退出角色会清除其他客户端的状态。修好交互后，这个问题会直接导致取到别人的角色页。

应按独立浏览器会话保存认领和阅读状态，网页 circuit 与图片 HTTP 请求解析同一会话。仅把服务改成 Scoped 不够，因为 circuit scope 与 HTTP request scope 不同。用多个独立 Cookie 容器和实际浏览器上下文，验证同时认领不同角色、访问相同页号、刷新和退出互不影响。

## 4. [P1] 多会话测试验证另写的假服务，无法覆盖实际程序

提交位置：`tests/LocalDm.Tests/TabletServiceSpikeTests.cs:99`，以及 `TabletConcurrencySpikeTests.cs` 中的内部 `SyntheticTabletService`。

两个测试文件分别定义自己的 HttpListener 服务，与 `src/LocalDm.Tablet` 的路由及会话实现不同。所谓“6 会话各取本人角色页”实际使用一个 HttpClient 请求六个不同 URL，没有认领角色，也没有独立浏览器会话。这些测试在实际程序共用一个角色的情况下照样通过。

测试内服务可以保留为明确标注的传输小实验，但不能拿它宣称实际应用的会话、刷新或 WPF 集成通过。应增加对真正 Tablet 应用的测试，使用独立会话请求相同页码，并断言各自正确图片。首次加载外壳、重复 HTTP GET 和浏览器刷新恢复是不同验证项，证据必须分别记录。

另需整理 `TabletBlazorHost`：第 29 行用 Directory.Exists 检查 DLL 文件，条件不会按预期识别现有发布产物；第 31 行在加入 publish 参数前就 Process.Start，随后再次 Start。固定共享临时发布目录也会让并发测试相互影响。启动流程应只有一次明确构建及一次启动，按测试隔离产物，并完整回收进程。

## 5. [P2] 原生 PDF 探针没有实际调用 API，第一步要求未完成

提交位置：`tests/LocalDm.Tests/WindowsPdfSpikeTests.cs:206`、`:209`。

NativePdf 的两个方法无条件抛出手写 NotSupportedException，对应两项测试断言这条异常存在。因此测试只能证明桩按写法失败，不能证明本机原生 API 实际不可用，也不能证明页数和逐页渲染经过试验。证据正确承认“未接入”，但据此把整个第一步称为“已完成”不成立。

此外，所谓压缩三页 PDF 样本没有有效的页树、交叉引用结构，且使用 DeflateStream 直接输出流。没有经真实 PDF 解析器验证，不能把其中 `/Count 3` 当成它确实是一份可阅读的三页 PDF。该测试至多表明字符串计数对这段构造数据返回 1。

应建立可由独立解析器验证的有效合成多页 PDF，实际调用原生 API 读取页数并渲染可解码图片；若接入失败，记录实际构建错误或 API 调用错误。方案第一步原本要求验证原生 PDF 与发布依赖，不能自行挪到第二步就称第一步完成。

## 修复后的交付要求

先修正探针、实际程序测试和证据，再重跑既定三条构建测试命令并提交中文修复提交。不能删除或增加跳过来规避上述失败；不应继续保留“原生渲染应该不可用”作为目标行为断言。

真实平板仍需用户操作，但应在桌面真实浏览器已经确认选角色、点卡、翻页可用，并且合成图片有效之后再邀请。设备浏览器品牌不限。WPF 同进程启停、局域网地址、防火墙和发布物尚未验证的项继续标未执行，不和 HttpListener 或子进程测试混为一项。

上述发现仅针对 `e4d46c2`。当前未提交修订即使已处理其中部分问题，也需形成明确提交并重新核对，不能据此改写本次提交审查结论。
