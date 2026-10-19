# Quickstart 验证复核（T066）

更新日期 2026-10-19。按 `specs/001-local-dm-mvp/quickstart.md` 逐节复核，记录实际结果。
真实剧本内容、密钥与录音不在本文件。

## 0. 构建与测试

```powershell
dotnet build LocalDm.slnx -c Debug
dotnet test tests\LocalDm.Tests\LocalDm.Tests.csproj
dotnet test tests\LocalDm.UiTests\LocalDm.UiTests.csproj
```

| 项 | 结果 |
|---|---|
| 解决方案构建 | **通过**，0 警告 0 错误（5 个应用/测试工程 + 1 个探针工具） |
| `LocalDm.Tests` | **94 项通过** |
| `LocalDm.UiTests` | **2 项通过**（真实 MainWindow + 真实 SQLite + 真实组合根） |
| 独立 WPF 窗口 | **出现**，标题「本局主持」 |

注：SDK 10 生成的是 `LocalDm.slnx`（新解决方案格式）；`LocalDm.sln` 不存在，等价可用。

## 1. US1 合成口播

| 检查点 | 结果 |
|---|---|
| 读取 loopback 模型列表 | 通过：`/v1/models` 200，56 个 model ID |
| 合成输入生成并朗读 | 通过：文本「欢迎五位侦探来到灯塔！请开始阅读第一幕。」，播音 10285 ms |
| 重播不重新生成 | 通过：0 ms 返回同一文本 |
| 停止/取消丢弃晚到结果 | 通过（自动化 5 项） |
| `/health` 不作为离线判据 | 通过（自动化：404 不影响模型列表语义） |

证据：`docs/evidence/us1/desktop-speech.md`、`desktop-speech-probe.json`、`host-100.png`。

## 2. US2 完整资料准备

| 检查点 | 结果 |
|---|---|
| 隐私预检（版本/app-owned home/遥测） | 通过：`disabled` 已确认，`unset` 不视为关闭 |
| 12 页文本 PDF 经 MinerU 解析 | **受阻**：`quality_tier_unavailable`（本机无 basic/standard/advanced 引擎） |
| 扫描 PDF / 图片 OCR | **未执行**（同一引擎阻塞） |
| 应用层覆盖与阻止开局 | 通过（11 项自动化） |
| 真实剧本准备为 Ready | **受阻**（全部为图片页，依赖 OCR） |

证据：`docs/evidence/us2/tool-parse.md`、`real-script-readiness.md`。

## 3. US3 完整确定性局

| 检查点 | 结果 |
|---|---|
| 真实窗口走完一局 | 通过：`Prepared → Playing → Revealed → Completed`，rev 0→8 |
| 关闭 MinerU 服务后仍可开局 | 通过（MinerU 服务本轮已停止，游戏不调用它） |
| 未勾选准备时下一步被拒 | 通过（界面提示「请先确认大家已口头准备好。」） |
| 同 operationId 重复只迁移一次 | 通过（10 次重试 → 1 条事件） |
| 揭秘前索取真相被拒 | 通过（`VOTE_INVALID` / 阶段条件；界面无真相文字） |
| SQLite 提交前后强退恢复 | 通过（3 个故障点回滚；重启恢复 rev=3 且不自动播音） |

证据：`docs/evidence/us3/flow-recovery.md`、`ui-session-steps.json`、`00-*.png`…`11-*.png`。

## 4. US4 OpenLess 与中文输入

| 检查点 | 结果 |
|---|---|
| 长中文（含冒号、逗号、角色名）写入 | 通过：47 字逐字符一致 |
| 清空重写不重复 | 通过 |
| IME 组合态 Enter 不误发 / Shift+Enter 换行 | 通过（自动化 6 项） |
| 状态意图只打开确认 | 通过（「这只是待确认提案…」，阶段未变） |
| 索取秘密 | 通过（安全回应，界面无真相） |
| 真实 OpenLess 语音落字 | **未执行**（需人工对麦克风说话） |
| 20 次发送/取消/重播压力 | **未执行** |

证据：`docs/evidence/us4/openless-input.md`。

## 5. US5 发布与安装

| 检查点 | 结果 |
|---|---|
| 自包含发布 | 通过：253 文件、247 DLL、含 `hostfxr.dll` |
| 无 SDK 环境启动 | 通过：0.8 秒出窗口，PATH 仅 System32 |
| 便携 ZIP | 通过：60.7 MB |
| per-user 安装器 | 通过：44.6 MB（Inno Setup 6.7.3） |
| 安装 → 启动 → 卸载 | 通过：退出码 0/0，安装目录被清除，用户数据保留 |
| 发布物不含 stories / 密钥 / 运行数据库 | 通过（脚本显式清理并校验） |
| 代码签名 | **未签名**，SmartScreen 可能提示未知发布者 |
| 真实 4–6 人整局 | **未执行** |

证据：`docs/evidence/us5/install-portable.md`、`fault-matrix.md`。

## 6. 输入规模与耗时（实测，非估算）

| 项 | 实测 |
|---|---|
| 解决方案构建（增量） | 约 4–6 秒 |
| 全部核心测试 | 约 1 秒 |
| 单个 WPF 界面测试 | 约 0.7 秒 |
| Magpie 模型列表请求 | 116 ms |
| 一次合成主持（模型 + 播音） | 10285 ms（含真实播音时长） |
| 重播 | 0 ms（不请求模型） |
| 发布物首次出窗口 | 0.8 秒 |
| 安装器编译 | 27 秒 |

以上为单次观测值，不作为性能承诺。

## 未解释的跳过项

无。所有未执行项都在各故事证据文件与本文中列出了原因（引擎受阻、需真人、需前台 GUI 缩放切换）。
