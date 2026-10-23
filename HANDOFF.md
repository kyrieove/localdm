# HANDOFF · 2026-10-23 · 平板加「手册」页签（设计已定，未动代码）

## 现在
- 已验证：线索 PDF 按页出平板卡、无文字手册页声明「不入包」放行（`390ac62`）。《加个V呗》已导入 `%LOCALAPPDATA%\LocalDm`（`pkg-2bd420786c76`，可开局，线索卡 9 张 2 组可用）。当时构建 0 警告，`LocalDm.Tests` 433 过 / 6 跳过，`LocalDm.UiTests` 59 过。
- 未验证：用户没在界面里点过导入向导（第 29 页「不入包」提示）；流程是 AI 草稿，用户没逐条确认；没有真实 iPad / 安卓平板实测（发言、整套界面、线索卡都没有），`docs/evidence/` 无记录，通过前不能说完成。
- 未提交：`tools/LocalDm.Probe/ImportWizardProbe.cs` 加了 `--data-root`、`--ignore-blank-pages`（用探针直接导入软件数据目录）。
- 旧的 `unreviewed-fix`：`LiveBoard.razor` / `Home.razor` 合并前请用户扫一眼。

## 下一步
1. 做「手册」页签（用户已逐条确认，不用再问）：
   - 页签放在现有页签旁，只有已认领角色的平板看得到；接口要求已认领角色，没认领 404（照角色本页）。不做 PIN，不加组织者身份，不做防误触确认。
   - 一页一页看原稿页（PDF 渲染成图），只读，无文字重排、不记阅读进度。
   - 复用 `RoleReader.razor`：图片地址写死成 `/tablet/role/source/<页>`，先加地址前缀参数。
   - 手册页现在不进资产：新增资产种类，在 `TabletPackagePreparer` 渲染（复用 `RoleSourceAssetBuilder.RenderPdfPages`），清单版本升到 3。包里 `SourceFile` 没有分组字段，手册在哪要靠 `<包>.flow.json` 的 `Layering.OrganizerGroup` 或段 ID `seg-{组}-p{页}` 反推。
   - 新写 `docs/decisions/0009-…`，改 ADR 0005 / 0008 与 `specs/…/tablet-distribution.md` 里「手册、真相不进入平板接口」，同步宪章原则 II 的说法。
   - 测试：公共投影和目录仍不含手册（注意 `TabletUiProjectionTests.cs` 的禁词 manual / handbook）；无会话、未认领 404，已认领 200（照 `TabletAppTests.cs:295`）。
2. 实机（只有用户能做）：`启动本局测试.cmd` 开一局，iPad 和安卓平板各扫码，验证发言、整套界面、线索卡，把设备型号、系统、浏览器版本、结果写进 `docs/evidence/tablet/`。
3. 探针：在 `src/LocalDm.Tablet` 目录里 `ASPNETCORE_ENVIRONMENT=Development dotnet run --no-build -- --urls http://127.0.0.1:5099`，再 `node tools/tablet-qr/verify-ui.mjs`（或 `verify-reader.mjs`）`--playwright-module <目录>`；每次重启服务。

## 先读
- `docs/decisions/0005`、`0007`、`0008`；`LESSONS.md`

## 已知缺口
- 整版多卡的图片剧本仍没有确认版式的界面：真实应用把所有组当单卡，平板上会发整张大图。
