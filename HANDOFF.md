# HANDOFF · 2026-10-25 · 修完试玩剩下的问题，用户说 OK 后统一测试、提交、发布

## 现在
- 已提交（未推送，都过了全套测试）：
  - `4afc8b3` 角色本进主持资料（ADR 0011），固定提示词按真人局问题重写（分段引导例外、手册没写如实说、缺卡请操作者核对、私密内容不外说）；`6332a0c` 设置页高级项只读看提示词；`7c5d306` 测试补齐。
  - `8ab9248` 长文分段朗读（≤ 500 字一段，Edge 某段失败从这段起用本地语音接着读）；`d6ae7ff` 默认上下文窗口 100 万。
  - `8cf210d` + `ff87719` 平板角色本翻卡：原稿默认盖着素笺卡背，按顺序长按约 0.65 秒翻开，点已翻开的图仍放大；全屏和文字重排只到已翻开页。合成模式浏览器实测过。
- **未提交**：`tools/LocalDm.Probe/PlaytestProbe.cs` 加了 `ReplyFor`，「无回复」会写清是「发送时主持台在忙」还是「软件提示了什么」。只编译过探针，没跑测试（用户要求：全部修完、用户说 OK 后再统一跑全套）。
- 「DM 偶尔不回复」已查：桌面端忙时发送按钮禁用、回车有 `CanExecute` 检查，文字留在输入框；平板忙时返回「主持正在回应」。**不是吞消息**。推测：探针那 2 次是模型报错或返回空内容（`HostViewModel.cs:1296` 上屏了非模型提示，探针只认模型回复）。验证：用新探针再跑一次试玩，看「无回复」后面写的原因。
- 未验证：以上都没在真软件、真平板里试过；桌面上装的还是旧版。

## 下一步
1. 继续修 `docs/playtest/2026-10-25-real-four-player-issues.md` 剩下的：第 4 条退出浏览器后找回角色（电脑上释放角色或认领页接管，要电脑确认）；第 2 条引导段做成流程里的按钮；暂缓线索找不到的原因（要看剧本包，先问用户）。第 6 条平板做成 App 要先问用户目标设备。
2. 2026-10-24 报告里还剩：主持台「已发放材料」在流程没发材料步骤时为空（低）。
3. 用户说 OK 后：`dotnet build LocalDm.slnx`、`dotnet test tests/LocalDm.Tests`、`dotnet test tests/LocalDm.UiTests` 一次跑完，再提交。
4. 发布（用户同意后）：`pwsh -File packaging/windows/Publish.ps1`，再 `artifacts/LocalDm-Setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /NORESTART /TASKS=desktopicon`。
5. 试玩确认「无回复」原因：`dotnet run --project tools/LocalDm.Probe -- --playtest --model gpt-6.1-sol`（约 45 分钟）。

## 先读
- ADR `docs/decisions/0010`、`0011`；`LESSONS.md` 末尾三条；卡 `docs/orch/2026-10-25-*`
- orchestrate 规则 `D:/2/dev/orch-runner/orchestrate/SKILL.md`
- 探针：`--context-size`（各剧本主持资料 token 数，只打印数字）、`--flip-preview --package <包文件名>`（真实原稿翻卡预览写到 `.local/`，含剧情）
- 新工作树缺 `fixtures/synthetic/.private/expected.json` 时 `ImportCoverageTests` 会红：`scripts/sync-local-fixtures.ps1 -Source D:\2\dev\dm`

## 已知缺口
- 翻卡只防误看，接口不拦截；长按不支持键盘；真平板长按手感没验。
- 整版多卡的图片剧本没有确认版式界面；全图片手册里声明「不入包」的空白页在平板手册里缺页。
