# HANDOFF · 2026-10-25 晚 · 平板 App（Flutter）接真电脑、用真剧本测试

## 现在
- 已验证：原型 `apps/tablet/` 用 iloader 装上了用户的 iPad Air 4，用户原话「还挺丝滑的」。按反馈修了三处（按住翻开盖页、横屏点哪页单独全屏、滑条按屏走），软件名暂定 **Dungeon Master**（21f3b95）。`flutter test` 12 项全过，C# 构建与 475 + 60 项测试全过。
- 未验证：修完后没重新装 iPad（用户要求全部做完再装）。原型仍是假数据，不连电脑。
- 怎么跑：设计说明与网址参数见 `docs/plans/2026-10-25-tablet-flutter-prototype.md` §1；工具链在 `.tools\`（Flutter 3.47.8、Android SDK、NDK 28.2），JDK 用系统 Zulu 17。
- 出包：apk 本机 `flutter build apk --release`；ipa 推 `apps/tablet/**` 到 main 后由 `.github/workflows/tablet-ipa.yml` 出未签名包，用户用 iloader 装。

## 下一步
0. 每个 shell 先设 `PUB_CACHE`、`ANDROID_HOME`（android-sdk）、`ANDROID_USER_HOME`（android-home）、`GRADLE_USER_HOME`（gradle），都在 `D:\2\dev\dm\.tools\` 下。
1. 先问用户真实剧本叫什么（他说的是「加个v呗」，没听准；库里记着《孽岛疑云》《K7-星火号》）。
2. 电脑端补接口缺口：原稿页数、手册页数、剧本状态、卡片有无背面、已翻开页数和「翻开」请求（后两项现在只在 Blazor 进程内，见 `TabletSpikeState.OpenedPages`）；加 JSON 契约测试。
3. 设备身份改请求头 `X-LocalDm-Device`（ADR 0012 细节 1）。
4. App 换真 HTTP 客户端，轮询 `/tablet/public`、`/tablet/ui`。
5. Flutter 网页版连本机电脑软件测：由平板服务托管 Flutter 网页版（同源，倾向）或加 CORS，开工时定。
6. 用真剧本跑通后，推送出 ipa，用户装 iPad。汇报不写剧情。

## 等用户
- logo：用户找 codex 生成后给图，换 `android/app/src/main/res/mipmap-*`、`ios/Runner/Assets.xcassets/AppIcon.appiconset`、`web/icons`。
- 仍未答：接口版本号与配对令牌做不做；投票上不上平板（原型按不上）；缓存（原型按内存 ±2 页）；平板播语音；网页端是否长期保留；电脑重启后认领是否持久化。

## 先读
- `docs/plans/2026-10-25-tablet-flutter-prototype.md`（§8 接口缺口）
- `docs/decisions/0012-tablet-native-flutter-app.md`（草案，未提交）
- `LESSONS.md` 末尾三条（IPv6、浏览器面板点 Flutter、对比度测试）
