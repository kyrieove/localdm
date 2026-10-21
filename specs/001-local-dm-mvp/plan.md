# Implementation Plan: 本局主持 Windows 单电脑 MVP

**Branch**: `001-local-dm-mvp` | **Date**: 2026-10-19 | **Spec**: [spec.md](spec.md)

**Input**: `specs/001-local-dm-mvp/spec.md`，取代早期平板和 SoloPlay 范围。

> **2026-10-21 平板分发增量（ADR 0005）**：本节新增局域网平板分发范围，实施顺序与证据以
> `docs/plans/2026-10-21-tablet-distribution.md` 为准。技术路线先验证 Blazor Interactive Server，
> 真实平板浏览器通过后才定型；失败时交付实测证据再修订，不自行更换技术栈。

## Summary

自建 WPF 软件，OpenLess 向原生文字框输入，Windows Magpie 普通文字模型返回公共主持文字，System.Speech TTS 朗读。
文档准备复用现有 MinerU；生成保留页块来源的冻结剧本包，正式游戏无需 OCR 服务。
规则核心决定阶段、材料提示、人工投票汇总和揭秘，公共模型没有状态写权限。
WPF 进程内可开启局域网 HTTP 服务，向本局平板只读分发公共对话与当前环节，并按认领角色提供角色本、按名称点选提供单张线索卡；服务默认关闭，只绑定局域网 IPv4。
当前完成的是规格、研究、设计及任务，不是应用实现。

## Technical Context

**Language/Version**: C# 14 / .NET 10，应用目标 net10.0-windows、Windows 11 x64。

**Primary Dependencies**: WPF、HttpClient、System.Text.Json、System.Speech、Microsoft.Data.Sqlite；MinerU 4.0.1 外部 CLI；用户已有 Magpie、OpenLess。实施时将实际可用稳定版本锁定到 Directory.Packages.props 和 packages.lock.json，不使用预览依赖。平板增量新增 ASP.NET Core 服务端网页（Blazor Interactive Server 为待验证候选）与二维码生成依赖，版本与许可在实施时核实后锁定。

**Storage**: 用户本地应用数据下单个 SQLite 文件，事件、状态投影和 operationId 在同一事务提交；原始材料和冻结剧本包独立只读文件，指纹绑定。密钥用 Windows DPAPI，日志脱敏。

**Testing**: xUnit 核心/契约/故障测试；真实 WPF 窗口通过 Windows UI Automation 和现场操作验证；发布包在无开发 SDK 的目标用户环境验收。

**Target Platform**: Windows 11 x64，单公共屏幕和音箱，无应用麦克风权限。

**Project Type**: desktop-app，应用默认不提供 HTTP 服务；平板分发开启后才在进程内监听局域网 IPv4。

**Performance Goals**: 普通控制立即显示工作/完成/失败状态；模型与文档处理异步可取消。实际 UI 响应、模型耗时、TTS 启播和整本准备耗时按输入规模记录，不编造上限或优化比例。

**Constraints**: 单次仅一段 TTS；不监听、不主动催促、不自动推进；真实材料未授权不外发。MinerU 配置隐私检查不确定时停止调用而不是尝试解析。

**Scale/Scope**: 一个 Windows 用户、一局 4–6 真人、一个固定剧本先验收。没有账户、平板、房间、多租户或 AI 玩家。（2026-10-21 增量：允许同局域网平板浏览器加入本局、认领角色、阅读私人材料、按名称点选线索卡及只读同步公共对话；仍不建设账户、跨公网房间或 AI 玩家。）

## Constitution Check

基准为宪章 3.0.0。以下为设计符合性，不代表运行验收通过。

| 原则 | Phase 0 前 | Phase 1 后设计证据 |
|---|---|---|
| 保真来源 | PASS，spec FR-005/006 | ImportJob、SourceSpan、冻结包和完整续读契约；Readiness 门禁 |
| 公共/秘密隔离 | PASS，FR-007/013 | Ui/PublicProjection 与 PublicContextBuilder 不暴露完整真相，拒绝未知外发 |
| 确定性恢复 | PASS，FR-008/010 | SQLite 原子事务、唯一操作、预期 revision 与恢复契约 |
| 单电脑按需主持 | PASS，FR-001/003/004 | 原生文字框、OpenLess、文本请求及独立 TTS；无采音/联网玩家端 |
| 既有工具与实证 | PASS，FR-012/013 | MinerU 隐私预检、真实 Windows 操作、安装包及真人整局任务 |

2026-10-21 平板增量（ADR 0005）符合性：原则 II 由平板公开消息投影与按 cardId 取图实现隔离，
角色本只经本人角色页接口提供，模型备忘与真相不进入投影；原则 IV 由进程内局域网服务与浏览器会话实现，
无账户、无公网、纸质模式保留。真实平板浏览器与发布物验收为待执行门禁，未通过前不标记 Ready。

无未解释的原则违例。纸笔汇总为明确的受限默认，开局显式接受且剧本允许，否则不支持该局。

## Project Structure

### Documentation (this feature)

```text
specs/001-local-dm-mvp/
  spec.md
  plan.md
  research.md
  data-model.md
  quickstart.md
  contracts/
    desktop-interactions.md
    integrations.md
    game-commands.md
    document-package.md
  tasks.md
```

### Source Code (repository root, planned)

```text
LocalDm.sln
global.json
Directory.Packages.props
src/
  LocalDm.App/
    App.xaml
    MainWindow.xaml
    Views/SetupView.xaml
    Views/LibraryView.xaml
    Views/ImportView.xaml
    Views/ReadinessView.xaml
    Views/HostView.xaml
    Views/AdvanceConfirmView.xaml
    Views/VoteSummaryView.xaml
    Input/HostInputController.cs
    ViewModels/HostViewModel.cs
  LocalDm.Tablet/
    TabletServiceHost.cs
    Endpoints/
    Pages/
    wwwroot/
  LocalDm.Core/
    Domain/
    Tablet/
    Import/ScriptValidator.cs
    Import/ReadinessProjection.cs
    Game/GameEngine.cs
    Game/CommandDispatcher.cs
    Hosting/HostDirector.cs
    Hosting/PublicContextBuilder.cs
    Ports/
  LocalDm.Infrastructure/
    Magpie/MagpieGateway.cs
    Documents/MineruRunner.cs
    Documents/MineruPrivacyPreflight.cs
    Documents/MineruContinuationReader.cs
    Import/ImportPipeline.cs
    Import/PreparedPackageReader.cs
    Import/SourceIndex.cs
    Import/TabletAssetBuilder.cs
    Import/GridCardCropper.cs
    Import/RoleBookRenderer.cs
    Storage/SessionStore.cs
    Storage/SettingsStore.cs
    Storage/SqliteSchema.sql
    Voice/TtsService.cs
tests/
  LocalDm.Tests/
  LocalDm.UiTests/
fixtures/synthetic/
packaging/windows/
docs/evidence/
```

**Structure Decision**: 三个应用项目分别承担 UI、纯业务、外部工具与存储；没有数据库服务器或通用插件平台。测试可以用固定合成包独立验证故事，不依赖真实剧本或远程模型。平板增量新增 `LocalDm.Tablet`（ASP.NET Core 宿主与页面，不依赖 WPF），由 WPF 在同进程启动和停止。

## Research Decisions

详见 research.md。采用 .NET 自包含发布、一个普通 Magpie 文本接口、Windows 已有中文音色和 OpenLess 文字落点。
存档从早期的「两个独立文件」收敛为 SQLite 事务；不改变用户单电脑范围。
首版 MinerU 为唯一已定位的文档工具适配器，pdf-inspector 非必需依赖。
不存在需要用户猜 API 的设计澄清。工具/模型可用性与实际质量未证明的部分是明确的实施验收任务，不能以文档代替通过。

## Delivery & Validation Order

Setup → shared foundation → US1 合成公共口播 → US2 完整剧本准备 → US3 确定性完整局 → US4 OpenLess 实机输入 → US5 安装与真人整局。
US2 与 US1 可在基础契约锁定后由不同文件所有者并行；US3 用合成固定包可独立测试，最终整局依赖 US1/US2。
US1 是中间可演示切片，不是完整 MVP；完整 MVP 必须覆盖 US1–US5。
详细执行顺序和验证由 tasks.md 定义，quickstart.md 提供实施后的运行场景。

## Complexity Tracking

无宪章违例。引入嵌入式 SQLite 仅为在一笔事务中保证事件与状态一致；两文件快照更需要恢复协议。
不实现流式语音、内置 ASR、通用 OCR 引擎、动态插件、联网身份和远程同步。
平板增量不引入账户系统、跨公网房间或 AI 玩家；局域网服务只在用户开启后绑定所选 IPv4，且不提供任意文件下载。
