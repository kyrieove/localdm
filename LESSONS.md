# LESSONS · 以后还会踩的坑

一条一行：坑 → 怎么认出来 → 怎么绕。决定在 `docs/decisions/`，当前进度在 `HANDOFF.md`。

- 派活卡 Risk 首词必须等于 `--risk` → lint 报 `Risk 'x' does not match` → 改卡用 Edit；high 的计划审查同一张卡最多两轮。
- 卡里只读路径写整目录（`src/LocalDm.App/`、`tests/`）→ 基线 Check 编译改 `bin/obj`，判 `readonly_violated`（lint 现已拒绝含 `.csproj` 的只读目录）→ 只列源码文件或无项目的子目录。
- 手写派活卡会漏 Don't 里的「Reuse existing functions」→ 一律从 `card-template.md` 复制。
- 基线 Check 没有针对新代码的用例时也会绿 → runner 打 WARNING → 验收另测。
- 测试里序列化投影不用 `UnsafeRelaxedJsonEscaping` → 中文被转义，防泄露断言失效 → 必须加。
- 并行整套测试偶发 `EdgeTtsTests` 探针超时、`HostConversationTests.Replay_holds_the_session_busy_gate` → 单跑稳定通过 → 根因未治（HANDOFF 已治：探测超时可由 LOCALDM_EDGE_PROBE_TIMEOUT_MS 覆盖，WaitUntilAsync 超时即失败）。
- Blazor 探针用 Production 启动 → 脚本没映射，页面空白；从仓库根目录启动 → 内容根不对，静态文件 404 → 在 `src/LocalDm.Tablet` 目录里用 `ASPNETCORE_ENVIRONMENT=Development dotnet run --no-build -- --urls http://127.0.0.1:5099`；每次探针重启服务以释放已认领角色。
- 发布带 `-r win-x64` 会弄脏 `packages.lock.json` → `--isolation worktree` 被拒 → `Publish.ps1` 现已在发布后还原。
- 旧 worktree/分支 `orch/tablet-speech-wiring`、`orch/tablet-speech-ui` 留在 `~/.claude/agent-live/orch-runner/wt/` → 内容已合入，删分支需用户确认。
- Python 替换脚本里写 Windows 路径，`\a` 会变成控制字符 0x07（`Assets\app.ico` → `Assets<BEL>pp.ico`），构建报 MSB4025 才发现：路径里的反斜杠一律用 `chr(92)`，改完 `grep -c $'\a'` 查一遍。
- 写卡时只 grep 了 `HostNarration*` 测试找「角色本不进 DM」断言 → 合入后全套测试 `PhaseFlowTests` 红了两条 → 改断言前对整个 `tests/` grep 断言里的常量和原句（`RoleBookText`、`合成角色的秘密`）。
- 卡在跑基线 Check 时，Claude 改了卡里列为只读的文件 → `readonly_violated`，执行者没启动 → 派出卡后到它跑完前，不碰卡的只读和可改路径。
- 试玩探针报「（无回复）」只认 `IsModelReply` → 模型报错时软件上屏的提示（`fromModel: false`）也被记成无回复，误以为消息被吞 → 探针现在用 `ReplyFor` 写清是忙时没发出、还是软件提示了什么。
