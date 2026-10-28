# HANDOFF · 2026-10-27 凌晨 · 12 项使用逻辑修复 + 换新名「绘合·Paletale」与新 Logo，本机已覆盖安装

## 现在
- 已验证：
  - 软件名已统一改为 **绘合·Paletale**，新 Logo（`apps/tablet/design/logo.png`，1254×1254）经 `make_icons.py` 生成电脑端 32 位 `app.ico` 与安卓/iOS/网页全套图标。
  - 真剧本《加个V呗》（`stories/2123-加个V呗/`，10-23 已导入本机软件，包 `pkg-2bd420786c76-1`）在电脑端能分发：4 个角色、9 张卡全部可用，角色本原稿 19 页，手册 29 页。
  - 平板服务补了接口：`storyState`、`hasBack`、`sourcePageCount`、`handbookPageCount`、`openedPages`、`generation`、`POST /tablet/role/open/{page}`、请求头 `X-LocalDm-Device`。契约测试在 `TabletAppTests`。
  - App 换成真 HTTP 客户端（`apps/tablet/lib/data/api.dart`），每 2 秒轮询。电脑托管网页版 `http://<电脑>:5188/app/`，无头 Chrome 实测：直接进阅读页，卡面 10 次请求全部 200，没有报错。
- 未验证：iPad/安卓真机连电脑（明文 HTTP 配置与相机权限已加，没上过真机）；选角色页真机点认领与真机摄像头扫码。
- 怎么跑：先构建网页版 `pwsh apps/tablet/tool/flutter.ps1 build web --release --no-web-resources-cdn --base-href /app/`，然后 `启动本局测试.cmd`，翻开剧本，平板服务会自动在 `/app/` 托管 `apps/tablet/build/web`。
- 真剧本验收：`scripts/open-story.ps1 -Title <剧本名>` 启动软件并翻开，`node scripts/probe-tablet-app.mjs` 用无头 Chrome 跑网页版，只汇总 `/tablet/*` 的状态码。

## 已修 bug（2026-10-26）
1. **剧本库「继续这一局」指向旧局**（`src/LocalDm.App/AppComposition.cs` `FindResumableAsync`）：已打开本局主持台（`_host?.Session.Package is not null`）时回剧本库不再显示「上一局还没走完」横条；未装本局时改按 `Store.ListSessionsAsync`（`updated_at DESC`）挑最近玩过的未完存档，不再按 `Revision` 最大值挑旧局。
2. **平板扫码接真相机 + 断连/换电脑入口**（`apps/tablet/lib/screens/connect.dart`、`waiting.dart`、`role_pick.dart`、`shell.dart`）：接入 `mobile_scanner` 调摄像头扫二维码（自动剥离 `http://` 与路径并补默认端口 `5188`），去掉点取景框进假数据的原型逻辑；等待页、选角页、断网横条补「换电脑 / 重新扫码」（`app.disconnect()`）；等待页区分在线等待开局与离线连接中，iOS 离线时提示检查「设置 › 隐私与安全性 › 本地网络」。
3. **平板右上角「原型调试」把手默认隐藏**（`apps/tablet/lib/data/app_state.dart`、`main.dart`）：`showDebug` 默认 `false`，仅 URL 带 `stage=` 或 `debug=1` 时开启并挂载 `DebugPanel` 抽屉；未连电脑时默认地址为空，等待开局时用 `TableUi.waiting()` 兜底而不回退假数据。
4. **使用逻辑顺手修**：
   - 真剧本包线索卡背面丢失（`src/LocalDm.Tablet/PackageTabletContent.cs`）：改用 `TabletQueries.BuildCatalog(_manifest)`，保留 `HasBack`。
   - 电脑端「重新开始本局」同步平板（`TabletWebHost.cs` `/tablet/catalog` 返回 `generation`）：平板轮询检测到 `generation` 变化自动清空旧认领并退回选角页。
   - 横屏双栏 0 张可用线索卡闪退（`apps/tablet/lib/screens/cards.dart`）：改用 `firstOrNull` 并显示空状态提示。
   - 纯文本角色本（`rolePageCount == 0`）防越界（`app_state.dart`、`reader.dart`、`sheets.dart`）：隐藏原稿切换、强制文字版、`clamp` 下界保护。
   - 阅读字号/行距/版式修改立即同步服务端（`app_state.dart` `updateReading`）。

## 2026-10-28
- 平板 App 公开对话页加了发言条（`apps/tablet/lib/screens/talk.dart`，调已有 `/tablet/say`、`/tablet/say/stop`），ADR 0012 第 2、3、4 条已修订（保留发言、不用 Tailscale）。只过了自动测试，没上真机。
- 发言条只在「公开对话」页；10-23 设计说「看角色本和线索卡时也能发言」，App 里还没做。
- 远程（隧道 + 口令 / 谷歌登录）只调研留档：`docs/plans/2026-10-28-tablet-talk-and-remote.md` 第二节。10-28 用户再次确认搁置：玩家都在一个屋，远程玩家听不到电脑语音也按不到说话键，先真机实测局域网。

- 整仓审查（sol）后修了 9 项，提交 `92613d0`。没修：OCR 失败文件不挡建包（「工具报错」和「空白插图页」现在分不开，直接挡会让正常剧本建不了包，待用户定）。
- deferred（复查 P2）：
  - `PublicMessagePublisher.Discard()` 不等在途写入，旧写入随后失败会把新局标成同步异常。修法：加代数，旧代数的失败忽略。
  - 导入向导只有条件照抄真相时，静默删条件且照样能建包。修法：留待复核标记，并补「只有条件命中」的测试。
- 平板 App 重开局清对话、MinerU 取消杀进程、备用 OCR 走 PATH：这三处没有自动测试。

## 下一步
1. 用户说了「推」再推送 main，由 `.github/workflows/tablet-ipa.yml` 出 ipa，用户用 iloader 装 iPad，连电脑实测（含发言）。Wi-Fi 有 AP 隔离（平板连不上电脑）时，先试电脑开 Windows「移动热点」让平板连热点，未验证。
2. 网页版要进发布包的话，打进 publish；现在只在源码目录里找。

## 等用户
- 仍未答：接口版本号与配对令牌；投票上不上平板；平板播语音；网页端是否长期保留；电脑重启后认领是否持久化。ADR 0012 草案未提交。

## 先读
- PC 界面已按平板「灯下」重做并合入 main（`f75f4a9`），已结束，不用接手：`docs/decisions/0013-pc-ui-dengxia.md`
- `docs/plans/2026-10-25-tablet-flutter-prototype.md` §8（已补的接口）
- `docs/decisions/0012-tablet-native-flutter-app.md`（草案）
- `LESSONS.md` 末尾
