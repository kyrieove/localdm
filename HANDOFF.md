# HANDOFF · 2026-10-25 夜 · 平板 App 已连上真电脑和真剧本，下一步出 ipa 上 iPad

## 现在
- 已验证：
  - 真剧本《加个V呗》（`stories/2123-加个V呗/`，10-23 已导入本机软件，包 `pkg-2bd420786c76-1`）在电脑端能分发：4 个角色、9 张卡全部可用，角色本原稿 19 页，手册 29 页。
  - 平板服务补了接口：`storyState`、`hasBack`、`sourcePageCount`、`handbookPageCount`、`openedPages`、`POST /tablet/role/open/{page}`、请求头 `X-LocalDm-Device`。契约测试在 `TabletAppTests`。
  - App 换成真 HTTP 客户端（`apps/tablet/lib/data/api.dart`），每 2 秒轮询。电脑托管网页版 `http://<电脑>:5188/app/`，无头 Chrome 实测：直接进阅读页，卡面 10 次请求全部 200，没有报错。
- 未验证：iPad/安卓真机连电脑（明文 HTTP 配置已加，没上过真机）；选角色页点认领（只用接口预认领测过）；相机扫码还是假的，要手动输入地址。
- 怎么跑：先构建网页版 `flutter build web --release --no-web-resources-cdn --base-href /app/`，要在 PowerShell 里跑（Git Bash 会改写 `/app/`）。然后 `启动本局测试.cmd`，翻开剧本，平板服务会自动在 `/app/` 托管 `apps/tablet/build/web`。
- 验收脚本思路：用无头 Chrome 加 CDP 只汇总 `/tablet/*` 的状态码，不截内容图（用户可能是玩家）。

## 下一步
0. 每个 shell 先设 `PUB_CACHE`、`ANDROID_HOME`、`ANDROID_USER_HOME`、`GRADLE_USER_HOME`，都在 `D:\2\dev\dm\.tools\` 下。
1. 用户说了「推」再推送 main，由 `.github/workflows/tablet-ipa.yml` 出 ipa，用户用 iloader 装 iPad，连电脑实测（手动输入地址）。
2. 真相机扫码（电脑二维码里是 `http://ip:port/`）。
3. 网页版要进发布包的话，打进 publish；现在只在源码目录里找。

## 等用户
- logo：换 `android/app/src/main/res/mipmap-*`、`ios/Runner/Assets.xcassets/AppIcon.appiconset`、`web/icons`。
- 仍未答：接口版本号与配对令牌；投票上不上平板；平板播语音；网页端是否长期保留；电脑重启后认领是否持久化。ADR 0012 草案未提交。

## 先读
- `docs/plans/2026-10-25-tablet-flutter-prototype.md` §8（已补的接口）
- `docs/decisions/0012-tablet-native-flutter-app.md`（草案）
- `LESSONS.md` 末尾
