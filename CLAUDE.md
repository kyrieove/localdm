# LocalDm 本局主持

Windows 单电脑剧本杀 AI 主持软件（.NET 10 + WPF），通过本机 Windows Magpie 调用模型。

**接手前先读根目录 `HANDOFF.md`**（从哪接着干）；用户已确认的需求和旧记录在 `docs/handoff-archive.md`，踩过的坑在 `LESSONS.md`。

## 必须遵守

- 不读取、不打印、不提交 `stories/`、`.appdata/`、`.local/` 下的文件：那里是剧本原文、含真相的剧本包和验收对话记录。仓库里的文档不写剧本正文、卡面或真相。
- 不删除、不跳过测试来让测试通过。唯一例外：`EdgeTtsTests` 里会真的出声的语音用例，用户授权在本机有中文系统音色时跳过。
- 提交前运行 `dotnet build LocalDm.slnx`、`dotnet test tests/LocalDm.Tests`、`dotnet test tests/LocalDm.UiTests`，全部通过再提交；提交信息用中文。
- 不为某一本剧本写专属规则或专属提示词措辞：软件要对任何剧本通用。
- 用户可能是这局的玩家：汇报还没玩过的剧本时，不出现任何剧情。
