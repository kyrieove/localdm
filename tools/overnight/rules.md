# 夜间自动任务：规矩（每个任务都适用）

你在 git worktree `{WORKTREE}` 里工作，分支 `{BRANCH}`。现在没有人在电脑前，不会有人回答你的问题。

1. 只在 `{WORKTREE}` 里改文件。不要切换分支，不要合并或推送任何分支，不要碰 `main`。
2. 先读 `CLAUDE.md` 和 `docs/handoff/2026-10-21-session-handoff.md`，再读任务里点名的方案章节。
3. 不读取、不打印、不提交 `stories/`、`.appdata/`、`.local/` 下的任何文件。测试只用合成数据。
4. 不删除测试，不加 Skip/Ignore 来让测试通过（`EdgeTtsTests` 那 6 条出声用例的已有授权除外，不要改它们）。
5. 只做任务里写的内容，不顺手改别的。方案和代码对不上、或者需要做任务范围外的决定时：**不要自己发挥**，在仓库根目录写 `OVERNIGHT-QUESTION.md` 说清问题，然后停下（不要提交半成品）。
6. 做完后依次运行并确认全部通过：
   `dotnet build LocalDm.slnx`
   `dotnet test tests/LocalDm.Tests`
   `dotnet test tests/LocalDm.UiTests`
   通过后提交到当前分支，提交信息用中文，写清改了什么、为什么。脚本之后还会再跑一遍这三条命令核对。
7. 不为某一本剧本写专属规则或措辞，软件要对任何剧本通用。不在仓库里写剧本正文、卡面或真相。
8. 模型接口报 429 时等一会儿再试，不要换模型路由；不要用 `group/auto…` 路由。
9. 不要启动 MinerU，不要启动需要真实剧本的探针（`--dm-acceptance`、`--import-wizard`）。
10. 最后在终端输出一段中文小结：改了哪些文件、新增哪些测试、三条命令的结果、还有什么没做完。
