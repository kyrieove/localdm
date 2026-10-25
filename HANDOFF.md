# HANDOFF · 2026-10-25 晚 · 平板 App 出了 Flutter 可点原型「灯下」，等用户试和定真机路径

## 一句话状态

minimax 那三个「书卷」方向用户否了（原话：「看起来像网页 而不是平板app；风格作废 你自己设计」）。我改做了一个**真 Flutter 工程的可点原型** `apps/tablet/`，网页版能在浏览器里点，19 张截图在 `docs/preview/tablet-app-flutter-20261025/shots/`。**没上过真机。** 网页端（Blazor）一行没动。

## 现在

- 设计说明（每屏组件、手势参数、状态表、缓存、对比度、接口缺口、验收）：`docs/plans/2026-10-25-tablet-flutter-prototype.md`。先读它。
- 已验证：`flutter analyze` 无问题；`flutter test` 9 项全过（对比度 + 导航断点 + 409 + 翻页 + 翻卡 + 断线缓存）；浏览器按 iPad Air 4（1180×820 / 820×1180）和安卓 1280×800 点过并截图看过。
- 未验证：任何真机。双指缩放、帧率、震动、常亮、相机扫码、iOS 本地网络弹框都没在设备上跑。
- 设备：用户 iPad Air 4（逻辑 820×1180 @2x）；朋友的 iPad 更新；安卓平板是高版本，型号未知。
- 假数据 `apps/tablet/assets/fixtures/*.json` 是合成服务真实返回（`ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/LocalDm.Tablet -- --urls http://127.0.0.1:5099` 录的）。
- 旧设计文件 `docs/plans/2026-10-25-tablet-flutter-app-design.md` 顶部已标「§5 三个方向作废」；`docs/preview/tablet-app-20261025/` 留作记录，不再用。

## 工具链（都在 `.tools\`，已 gitignore）

- Flutter 3.47.8 稳定版：`D:\2\dev\dm\.tools\flutter`（官方 zip，SHA256 已核对）。每次用前设 `PUB_CACHE=D:\2\dev\dm\.tools\pub-cache`。
- Android SDK：`.tools\android-sdk`（cmdline-tools、platform-tools、platforms/android-36、build-tools/36.0.0、ndk/28.2.13676358；许可用户已同意）。新版 sdkmanager 包名用斜杠：`platforms/android-36`。
- JDK 用本机 Zulu 17（`C:\Program Files\Zulu\zulu-17`，系统已装，不是本轮装的）。
- 出 apk 前设：`ANDROID_HOME=D:\2\dev\dm\.tools\android-sdk`、`ANDROID_USER_HOME=…\.tools\android-home`、`GRADLE_USER_HOME=…\.tools\gradle`、`PUB_CACHE=…\.tools\pub-cache`，然后 `flutter build apk --release`（约 2 分钟，52.8MB，三种 CPU 架构合一）。
- 本机 IPv6 不通：aria2 下载会失败，用 `curl -4`。

## 下一步

1. 用户在浏览器里看过原型，评价「还不错」。原型 apk 已出：`artifacts\本局主持-原型.apk`（gitignore，不进仓库），等用户装到安卓平板上试手感并反馈。原型用假数据，不连电脑；右上角烧瓶按钮切状态。
2. iPad：`.github/workflows/tablet-ipa.yml` 已跑通（`apps/tablet/**` 有改动推到 main 就自动出包，也能手动触发），首个包 run 40225880512，下载到 `artifacts\本局主持-原型-未签名.ipa`（11MB）。等用户用 iloader 装到 iPad Air 4 试手感；装没装上、哪一步卡住都还没反馈。仓库已推送到 origin/main。
3. 补四个接口缺口（设计文件 §8）：原稿页数、手册页数、剧本状态、卡片有无背面；加 JSON 契约测试。
4. 更新 ADR 0012：写进用户对下面问题的回答。

## 还等用户回答

1. **跑真包用哪本剧本**：「加个v呗」没听准；仓库有《孽岛疑云》与《K7-星火号》。
2. **接口版本号 + 配对令牌**这次做不做。
3. **投票上不上平板**（原型按不上）。
4. **缓存**：原型按「只留内存、当前页 ±2」做；全书缓存翻得更快，但散局后全本留在朋友平板上。
5. ADR 0012 原有的：平板播放语音一期做不做（原型没做）；网页端是否长期保留（倾向保留，用户没确认过）；电脑重启后认领是否持久化（建议做）。

## 已知缺口（与平板有关）

- 翻卡只防误看，接口不拦截；页面没按环节解锁，全本都发到平板。
- ADR 0007 平板发言待删（ADR 0012 决定 4）：测发言的用例随功能移除，是为删功能删测试。
- 网页端 `--muted #716D63` 在纸深底上 4.41:1 不达标，没改。
- 原型的夜读对比度用占位页颜色算的，真实扫描页要抽样复核。

## 先读

- `docs/plans/2026-10-25-tablet-flutter-prototype.md`、ADR `0012`
- 试玩记录 `docs/playtest/2026-10-25-real-four-player-issues.md` 第 4、6 条
