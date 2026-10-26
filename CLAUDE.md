# LocalDm 本局主持

Windows 单电脑剧本杀 AI 主持软件（.NET 10 + WPF），通过本机 Windows Magpie 调用模型。

**接手前先读根目录 `HANDOFF.md`**（从哪接着干）；用户已确认的需求和旧记录在 `docs/handoff-archive.md`，踩过的坑在 `LESSONS.md`。

- 用户说的剧本名，先对 `stories/` 下的目录名（只列名字）。软件真正在用的数据在 `%LOCALAPPDATA%\LocalDm\packages`，`.appdata/` 是测试数据；核对是哪本只 grep `"Title"` 字段（值是 `\u` 转义的），不读正文。
- 平板 App 的 Flutter 命令走 `pwsh apps/tablet/tool/flutter.ps1 <参数>`，它会设好 `.tools\` 下的环境变量。
- 同时开两个会话时各用各的 worktree，不在同一个工作区里并行改。

## 必须遵守

- **对用户说的每一句话都用中文**，包括工具调用之间的一两句进度说明、后台任务回调后的播报、问题轮次。技能名、路径、代码、命令保持原样。用户已多次指出长会话里会滑成英文，每条消息发出前检查。

- 不读取、不打印、不提交 `stories/`、`.appdata/`、`.local/` 下的文件：那里是剧本原文、含真相的剧本包和验收对话记录。仓库里的文档不写剧本正文、卡面或真相。
- 不删除、不跳过测试来让测试通过。唯一例外：`EdgeTtsTests` 里会真的出声的语音用例，用户授权在本机有中文系统音色时跳过。
- 提交前检查由 `.claude/hooks/pre-commit-checks.ps1` 自动跑：改了 `src/`、`tests/` 跑 dotnet 构建与两套测试，改了 `apps/tablet/` 跑 flutter analyze 与 test，不过就挡住提交。新 worktree 先跑 `scripts/sync-local-fixtures.ps1`。提交信息用中文。
- 不为某一本剧本写专属规则或专属提示词措辞：软件要对任何剧本通用。
- 用户可能是这局的玩家：汇报还没玩过的剧本时，不出现任何剧情。
