# 夜间自动跑（多个编码助手并行）

睡前开跑，`overnight.ps1` 会自动完成下面这些事：

1. 给每个助手（DeepSeek、Gemini、MiniMax）建一个 git worktree 和对应的 `agent/<名字>` 分支，已经建过的直接复用。
2. 每个助手按 `queue/<名字>/` 里的任务依次做。每个任务的提示词前面会自动加上 `rules.md` 里的规矩。
3. 每个任务做完后，脚本自己检查一遍：
   - 有没有删测试、加 Skip、碰 `stories/` 这几个目录、换分支；
   - 依次运行 `dotnet build LocalDm.slnx`、`dotnet test tests/LocalDm.Tests`、`dotnet test tests/LocalDm.UiTests`。三个助手共用一把锁轮流跑，免得界面测试互相抢窗口。
4. 检查没通过时，把错误日志末尾交给助手重试，最多 `maxAttempts` 次。还是不行，就把这次的改动保存到 `failed/<名字>/<任务>` 分支，助手分支退回任务开始前，再接着做下一个任务。
5. 助手遇到需要人来决定的事，会写 `OVERNIGHT-QUESTION.md` 后停下。脚本把问题收起来，早上一起看。
6. 全部做完后，把各个 `agent/*` 分支依次合进 `overnight/integration` 分支（基于 `main`），再整体跑一遍三条命令。
7. 写一份报告到 `D:\2\dev\dm-overnight\report.md`。

**脚本绝不合并或推送 `main`，也不推送任何分支。** 跑的过程中电脑不会睡眠，跑完自动恢复。

## 睡前要做的（大约 15 分钟）

### 1. 拿到脚本

```powershell
cd D:\2\dev\dm
git checkout main
git pull origin main
git fetch origin claude/vibrant-cray-q0vxyz
git merge --no-ff origin/claude/vibrant-cray-q0vxyz   # 只加了 tools/overnight 和 .gitignore 两行
```

### 2. 填助手命令

```powershell
copy tools\overnight\agents.example.json tools\overnight\agents.json
notepad tools\overnight\agents.json
```

`agents.json` 已加进 .gitignore，不会被提交。

每个助手要满足两个条件：**能无交互运行**（从标准输入读提示词，做完自己退出），并且**自动批准改文件和运行命令**（没人在电脑前帮它点确认）。

| 字段 | 含义 |
|---|---|
| `exe` | 程序名。`.cmd`/`.bat` 形式的命令行工具（npm 装的大多是）写成 `cmd.exe`，再把真正的命令放进 `args` |
| `args` | 参数。里面的 `{PROMPT_FILE}` 会被替换成提示词文件的路径，给不支持标准输入的工具用 |
| `stdinPrompt` | `true` 表示把提示词从标准输入喂给它 |

- **Gemini CLI**：示例里已经填好（`gemini --yolo -p ...`，提示词从标准输入读）。
- **DeepSeek、MiniMax**：填它们各自的"无交互 + 自动批准"参数。例如用 Claude Code 接其他模型时，写 `"exe": "cmd.exe", "args": ["/c", "claude", "-p", "--dangerously-skip-permissions"]`。具体以你用的工具的文档为准。
- 只想跑两个助手的话，把第三个从 `agents` 里删掉就行。

### 3. 自检（一定要跑）

```powershell
powershell -ExecutionPolicy Bypass -File tools\overnight\overnight.ps1 -Check
```

自检会建好工作文件夹，并让每个助手只回复一句 OK，看能不能在 5 分钟内无交互地跑完。显示"自检通过"才往下走；没通过就按提示看对应的日志。

### 4. 决定今晚跑多少

- `queue/` 里是**三个小问题**（交接文档 5.4 节），不受平板门槛限制，默认就会跑。
- `queue-gated/` 里是**平板第二步、第四步的存储层和规格同步**。方案规定真实平板验收通过后才能做这些，所以默认不跑。如果你决定先做这些和浏览器技术无关的部分，把 `agents.json` 里的 `includeGated` 改成 `true`。做出来的东西仍然要等平板验收后才能合进 `main`。

### 5. 开跑，然后去睡觉

```powershell
powershell -ExecutionPolicy Bypass -File tools\overnight\overnight.ps1
```

窗口不要关。电源接好；如果合上笔记本盖子会睡眠，就别合盖。

## 早上起来

1. 打开 `D:\2\dev\dm-overnight\report.md`，每个任务的结果是以下四种之一：

   | 结果 | 含义 |
   |---|---|
   | 成功 | 已提交到 `agent/<名字>` 分支 |
   | 失败 | 改动保存在 `failed/<名字>/<任务>` 分支 |
   | 提问 | 助手的问题在 `logs\*-question.md` 里 |
   | 无改动 | 助手什么都没改 |

   报告最后一段是试合并的结果。
2. 审查改动（交给 Codex 也行）：

   ```powershell
   cd D:\2\dev\dm
   git log --oneline main..overnight/integration
   git diff main...overnight/integration
   ```

3. 没问题就合进 `main`，跑三条命令，再推送：

   ```powershell
   git merge --no-ff overnight/integration
   dotnet build LocalDm.slnx
   dotnet test tests/LocalDm.Tests
   dotnet test tests/LocalDm.UiTests
   git push origin main
   ```

4. 准备下一晚：在每个工作文件夹里执行 `git merge main`，再把新任务放进 `queue/<名字>/`。文件名按顺序编号，例如 `03-xxx.md`。已经跑过的任务记在 `D:\2\dev\dm-overnight\state\` 里，不会重复跑；想重跑某个任务，就从对应的 `.done.txt` 里删掉那一行。

## 写新任务时注意

- 一个任务只做一件能在 1–2 小时内做完的事。写清楚改哪些文件、不准改哪些文件、要补什么测试。
- 同一晚的不同助手不要改同一个文件。实在避不开，就在任务里写明各自负责哪一段。
- 需要真实剧本的事（`--dm-acceptance`、`--import-wizard`、MinerU）**不要排进夜间队列**，留给你在主文件夹手动做。
