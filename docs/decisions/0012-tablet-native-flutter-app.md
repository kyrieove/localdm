# 0012 平板做成原生 App：Flutter 一套代码出 apk 和 ipa，仍走同一局域网

日期 2026-10-25，2026-10-28 修订第 2、3、4 条。状态：方向已由用户确认，App 已实现并接入真实接口（见 `docs/plans/2026-10-25-tablet-flutter-prototype.md` §8）；真实 iPad 与安卓平板实测未做。本文件不含剧本正文、卡面或真相。

## 背景
试玩记录 `docs/playtest/2026-10-25-real-four-player-issues.md` 第 6 条给出三个动机：全屏、常亮、Cookie 丢失。现状（`src/LocalDm.Tablet`）：电脑内嵌 Blazor Server，局域网 HTTP，端口优先 5188、被占时随机回退；设备身份是浏览器 Cookie（`TabletSessions.cs`，`MaxAge` 1 天）；认领状态只在电脑内存（`TabletSpikeState.cs`）。

用户要原生 App，原话：“如果软件是套网页那就没有必要做软件了，软件的优点是很丝滑，不用受制于浏览器。”平板服务的数据已全部以 JSON 和图片接口提供（`/tablet/catalog`、`/tablet/cards/*`、`/tablet/role/book`、`/tablet/role/progress`、`/tablet/role/source/*`、`/tablet/handbook/source/*`、`/tablet/public`、`/tablet/ui`），Blazor 页面只是其中一个客户端；原生 App 作为第二个客户端接入同一套接口。

## 决定（用户已确认）
1. **原生界面，用 Flutter**：一套 Dart 代码出安卓 apk 和 iPad ipa；界面自己画，不嵌网页。放在仓库 `apps/tablet/`，不进 `LocalDm.slnx`。
2. **仍走同一局域网 HTTP**。（2026-10-28 修订：用户不用 Tailscale；以后要远程时走 Cloudflare 隧道 + 口令或谷歌登录，调研记录在 `docs/plans/2026-10-28-tablet-talk-and-remote.md` 第二节，暂不开工。）
3. **不做任何账号登录**（局域网阶段不做；远程阶段可能加谷歌登录，见上条）。
4. **保留平板发言，App 接入**（2026-10-28 修订，原草案「删掉平板发言」作废，从未执行）：App 调已有的 `/tablet/say`、`/tablet/say/stop`，规则沿用 ADR 0007（先认领、主持忙时不排队不打断、300 字、2 秒间隔、推进类只到提案）。语音用系统键盘听写。网页发言条 `Pages/SpeechBar.razor` 保留。
5. **不上架、不买苹果开发者账号**：
   - 安卓：Windows 本机编译 apk，直接发给朋友。
   - iPad：仓库是公开的，用 GitHub Actions 免费的 macOS 机器执行 `flutter build ios --release --no-codesign`，打包成未签名 ipa；ipa 发在 GitHub Releases；**每个玩家在自己电脑上装 [iloader](https://github.com/nab138/iloader)（Windows 需 iTunes、数据线），用自己的免费 Apple ID 签名，装到自己的 iPad**。用户确认大家都有电脑、会操作。

## 设计细节（待用户确认）
1. **设备身份改成 App 自己的设备编号。** App 首次启动生成随机编号存在本机，每个请求带请求头 `X-LocalDm-Device`；电脑端优先认请求头，没有时仍认 Cookie，网页客户端不受影响。杀掉 App、隔天再开都还是同一台设备，Cookie 丢失问题消失。
2. **连接电脑**：App 内扫电脑上的二维码（相机扫码），或手输地址；地址存在本机。电脑 IP 变了就重新扫一次。iOS 要在 `Info.plist` 声明本地网络用途（`NSLocalNetworkUsageDescription`）并允许局域网明文 HTTP（`NSAllowsLocalNetworking`），首次连接系统会弹“允许访问本地网络”；安卓配 network security config 放行明文 HTTP。
3. **全屏和常亮**：App 运行时保持屏幕常亮、沉浸式全屏，两端都由 App 自己做。
4. **功能对齐现有网页**：选角色 → 角色本（原稿翻页 / 文字重排，同步阅读进度）、线索卡（长按翻面）、手册、公开对话只读、当前环节。原稿页大图要支持流畅翻页和双指缩放，这是选 Flutter 的主要理由。
5. **接口契约固定下来**：现在接口返回匿名对象，改字段网页能跟着改，App 不能。在 `tests/LocalDm.Tests` 加契约测试，固定这些 JSON 的字段名和类型；接口加版本号，App 发现版本不匹配时提示更新。
6. **保密边界不变**：App 只能拿到现在平板接口给的内容，真相、模型备忘不进任何接口。

## 待确认问题
1. 主持语音推到平板，一期做不做？同屋多台同时播会有回声；建议一期只用电脑播放，平板播放和远程阶段一起做。
2. 现有平板网页保留作后备（没装 App 的设备扫码还能用），还是删掉？建议保留，删除也是额外工作。
3. App 在本机缓存角色本和卡面图片吗？缓存翻页更快，但散局后图片留在朋友的平板上。建议只缓存在内存里，释放角色或退出 App 就清掉。
4. 电脑重启后认领会丢（状态只在内存），要不要持久化？
5. 实测设备：iPad 型号与 iPadOS 版本、安卓平板型号与系统版本。

## 否掉的方案
- 主屏幕网页 App（PWA）和网页套壳：用户认为受制于浏览器、不够丝滑；iPad 网页在 HTTP 下也做不到常亮。
- .NET MAUI：能和电脑端共用 C#，但大图翻页的流畅度不如 Flutter；未签名 ipa 的出包流程未核实。
- Swift + Kotlin 各写一套：最流畅，但工作量翻倍。
- 谷歌登录：局域网 IP 不能作为谷歌登录来源；局域网阶段不做（远程阶段有固定域名后可做）。
- 苹果开发者账号（99 美元/年）、Tailscale（含 Funnel）：用户不采用。

## 后果
- **iPad 版 7 天过期**（免费 Apple ID 的限制），每台 iPad 最多同时装 3 个侧载 App。每人在自己家用 iloader 重签；或另装 SideStore 在 iPad 上自己续签（占一个名额）。要写一页安装与重签说明。
- 本机要装 Flutter SDK、Android SDK 和 JDK（目前都没有）。**都装在项目文件夹里（如 `.tools/`，不进 git），不放 C 盘**；pub 缓存、Gradle 缓存、Android 用户目录默认在 C 盘，要用 `PUB_CACHE`、`GRADLE_USER_HOME`、`ANDROID_HOME`、`ANDROID_USER_HOME` 指到项目里，由一个脚本统一设好；新增 GitHub Actions 工作流出 ipa；App 的测试用 `flutter test` 单独跑。项目 `CLAUDE.md` 的提交前检查要加上“改了 `apps/tablet/` 时跑 `flutter test`”。
- 发言保留：`TabletSpeechTests`、`TabletSpeechE2ETests` 不动；`TabletAppTests` 补 App 设备头发言的契约。宪章原则 IV（平板可发言）不用改。
- 重新验证：自动测试之外，用真实 iPad 和安卓平板逐项实测：扫码连接、全屏、常亮不熄、杀掉 App 再打开仍是原角色、隔天仍是原角色、电脑重启软件后能重连、原稿翻页与缩放流畅、iloader 安装和 7 天后重签。记录设备型号、系统版本和结果；通过前不能说功能完成。
