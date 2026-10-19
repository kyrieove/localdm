# Quickstart validation guide

当前为设计阶段。下面命令在 tasks.md 对应实现后执行，不能把它们当作已经可运行或已通过。
仓库当前没有 LocalDm.sln、应用源码或安装包。

## Prerequisites

Windows 11 x64；实施时安装 .NET 10 SDK 并锁定实际稳定版本；开发依赖按 Directory.Packages.props 还原。
用户已有 Windows Magpie、OpenLess 和中文系统声音；Magpie 下游网络由用户配置决定。
MinerU 4.0.1 仅准备阶段使用，应用 profile 的本地引擎及遥测/规则状态须经设置确认。
真实剧本输入位于被忽略的 stories 或用户外部路径，未允许前只用 fixtures/synthetic。

## Build and run after implementation

在仓库根目录 PowerShell 执行：

```powershell
dotnet restore LocalDm.sln
dotnet build LocalDm.sln -c Release --no-restore
dotnet test LocalDm.sln -c Release --no-build
dotnet run --project src/LocalDm.App/LocalDm.App.csproj
```

预期是构建成功、规则/来源/故障测试通过并出现独立 WPF 窗口。测试失败或缺工具要明确记录，不默认跳过。

## US1 合成口播

设置实际 Magpie loopback 地址并加载 model ID；不要以 /health 404 判离线。
用「请只回复：连接成功」做合成连接测试。选择中文声音，点击合成场景开场。
确认显示与播音，停止和重播均不写玩法状态；取消后晚到结果不会显示或播放。
保存 docs/evidence/us1 中的脱敏请求、界面截图与实际设备结果。

## US2 完整资料准备

先运行工具隐私预检。规则/引擎/遥测不明时不得提交文件，不能自动切到远程。
导入超过 10 页的合成 PDF、扫描 PDF 及图片文件夹；核对 manifest 和所有续读范围。
再分别导入缺页、冲突、未续读、源 hash 错误和仅 Markdown 文件；必须不能开局。
公共报告与日志不出现秘密或答案。记录工具版本、覆盖和来源，不能以 OCR 文本不为空作为保真证据。

## US3 完整确定性局

使用已验证的合成冻结包，关闭 MinerU 服务后仍可开局。
未勾选准备时下一步被拒绝；满足条件再提交，连续重复同 operationId 只迁移一次。
录入纸笔汇总、确认并按原本结算；在揭秘前索取真相必须被拒绝。
在 SQLite 提交前后故障点强退，再次运行恢复前一或后一完整状态，不出现半提交、不自动重播。

## US4 OpenLess 与中文输入

焦点放到实际 WPF TextBox，按用户已有 OpenLess 快捷键，说含角色名、标点的合成句子，回车发送。
用中文 IME 确认候选时不能误发，Shift+Enter 换行；若外部工具只写剪贴板，支持正常粘贴后发送。
20 次发送、取消、停止、重播操作记录无丢字/重复和重叠播音。应用不请求麦克风。
「下一阶段」文本只能打开确认，不能直接执行。

## US5 Publish and install

```powershell
dotnet publish src/LocalDm.App/LocalDm.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
```

用 packaging/windows/LocalDm.iss 构建 per-user 安装器，将同一发布目录生成便携 ZIP。
实际命令由已安装并锁定的安装器工具路径执行；不要假称当前机器已有该工具。
测试用户无开发 SDK 也能启动；系统声音、Magpie、OpenLess 缺项显示准确。发布物不得包含 stories、密钥或运行库数据。
仅在获准资料处理后，用真实 4–6 玩家完整玩一局；另测断网、输入工具退出、TTS 故障与强退恢复。
安装器签名或系统信任问题如实报告，不要求绕过安全警告。

## Evidence and completion

每个故事保存 scenario、版本/commit、命令、实际结果、截图及未测项，真实私密内容不进报告。
完整发布验收按 SC-001–SC-006，US1 单独完成只是首个切片。
无人可用的真人测试时明确未执行，不能由 Mock、HTML 或自动化替代。
