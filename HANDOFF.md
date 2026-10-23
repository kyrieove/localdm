# HANDOFF · 2026-10-23 · 平板「组织者手册」页签已做完，等用户实机试

## 现在
- 已验证：手册页签已提交（`6eaac94`，ADR 0009）。构建 0 警告，`LocalDm.Tests` 441 条通过（含跳过的），`LocalDm.UiTests` 59 条通过。已经打成安装器（`artifacts/LocalDm-Setup-x64.exe`），并静默装到了 `%LOCALAPPDATA%\Programs\LocalDm`，桌面上有「本局主持」快捷方式。装好的版本启动后能监听 5188 端口，平板页面返回 200，没认领角色去取手册页返回 404。
- 未验证：真实剧本包上手册识别得对不对（《加个V呗》下次打开时会因为清单版本升到 3 而自动重新准备，手册应该是 29 页）；没有真实 iPad / 安卓平板实测（发言、整套界面、线索卡、手册都没有），`docs/evidence/` 没有记录，通过前不能说完成；用户没在界面里点过导入向导；流程是 AI 草稿，用户没逐条确认。
- 已知限制：装好的版本不会跟着代码自动更新，改代码后要重跑 `pwsh -File packaging/windows/Publish.ps1`，再运行 `artifacts/LocalDm-Setup-x64.exe`（装过一次后，重装会沿用原来的目录）。`启动本局测试.cmd` 仍然可用，它跑的是仓库里 `artifacts/publish` 那一份。
- 只有图片、没有文字的手册页如果被声明「不入包」，这一页没有来源 span。PDF 手册不受影响，因为是整份文件逐页渲染；图片手册会漏掉这一页。
- `specs/001-local-dm-mvp/spec.md` 的 FR-023 说「公共网页响应不含手册」：手册接口要求已认领角色，所以不算公共响应，没改这条。
- 旧的 `unreviewed-fix`：`LiveBoard.razor` / `Home.razor`，合并前请用户扫一眼。

## 下一步
1. 用户自己试：点桌面的「本局主持」开一局，用平板扫码认领角色，看「组织者手册」页签翻页是否正常；iPad 和安卓各试一次，验证发言、整套界面、线索卡和手册，把设备型号、系统、浏览器版本和结果写进 `docs/evidence/tablet/`。
2. 用户报问题就修，修完重发布、重装（命令见上）。

## 先读
- `docs/decisions/0005`、`0007`、`0008`、`0009`；`LESSONS.md`
- 探针：在 `src/LocalDm.Tablet` 目录里 `ASPNETCORE_ENVIRONMENT=Development dotnet run --no-build -- --urls http://127.0.0.1:5099`，再 `node tools/tablet-qr/verify-ui.mjs`（或 `verify-reader.mjs`）`--playwright-module <目录>`；每次重启服务。

## 已知缺口
- 整版多卡的图片剧本仍没有确认版式的界面：真实应用把所有组当单卡，平板上会发整张大图。
