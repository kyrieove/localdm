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
- 普通 `dotnet build` 之后跑 `Publish.ps1` → `NETSDK1047`（assets 里没有 `win-x64` 目标）→ 先 `dotnet restore src\LocalDm.App\LocalDm.App.csproj -r win-x64` 再发布，事后还原它弄脏的 `LocalDm.Core/packages.lock.json`。
- `make_icons.py` 出的 ico 是 24 位 → WPF 窗口一加载就 `XamlParseException`（指向 `Icon=`），测试没拦住 → 存成 RGBA；换图标后要真启动一次 exe。
- 旧 worktree/分支 `orch/tablet-speech-wiring`、`orch/tablet-speech-ui` 留在 `~/.claude/agent-live/orch-runner/wt/` → 内容已合入，删分支需用户确认。
- Python 替换脚本里写 Windows 路径，`\a` 会变成控制字符 0x07（`Assets\app.ico` → `Assets<BEL>pp.ico`），构建报 MSB4025 才发现：路径里的反斜杠一律用 `chr(92)`，改完 `grep -c $'\a'` 查一遍。
- 写卡时只 grep 了 `HostNarration*` 测试找「角色本不进 DM」断言 → 合入后全套测试 `PhaseFlowTests` 红了两条 → 改断言前对整个 `tests/` grep 断言里的常量和原句（`RoleBookText`、`合成角色的秘密`）。
- 卡在跑基线 Check 时，Claude 改了卡里列为只读的文件 → `readonly_violated`，执行者没启动 → 派出卡后到它跑完前，不碰卡的只读和可改路径。
- 试玩探针报「（无回复）」只认 `IsModelReply` → 模型报错时软件上屏的提示（`fromModel: false`）也被记成无回复，误以为消息被吞 → 探针现在用 `ReplyFor` 写清是忙时没发出、还是软件提示了什么。
- 设计稿里用程序化 SVG 画「扫描页」，同一个 `linearGradient` id 既当纸底又当最后一张蒙层 → 蒙层把整页文字盖住，截图只剩一片米色，乍看像渲染失败 → 渐变只能引用一次；要做纹理另起 id 或只用 `<filter>` 噪声，截图后一定要用看图能力亲眼看一遍。
- `design_lint.py check` 传 `file://` URL → 报「relative path can't be expressed as a file URI」→ 它要本地路径，不要 URL；Node 版 `playwright screenshot` 才吃 `file://`。
- `design_lint.py` 依赖 **Python 版** playwright，Node 版不算 → `pip install playwright pillow` 后设 `PLAYWRIGHT_BROWSERS_PATH` 指向 `.tools\playwright-browsers`，就复用已下的 chromium，不必再下一份（否则默认落 `%LOCALAPPDATA%`，进 C 盘）。
- 临时日志重定向到 `.local/`（探针 stdout/err）→ 那是禁读禁提交的目录 → 临时产物一律写 `artifacts/`（已 gitignore）或系统临时目录，别往 `.local/` 里塞。
- `design_lint.py check` 的字号下限是网页口径（正文 ≥14px、标签 ≥12px）→ 平板 App 的界面也在 1:1 设备像素里画，11sp 的标签是真偏小 → 按 App 口径直接提到 12/14，别当误报放过；`compare` 报「撞了」时它看的是整个 HTML（含展示板外壳），三版外壳一致会误判，板面底色与构图拉开才解决。
- aria2 下 Flutter SDK 报「unreachable network」→ 本机 IPv6 不通 → 大文件用 `curl -4`；Bash 里直接写 curl 会被 context-mode 钩子拦，写成 node/python 脚本再跑。
- 在浏览器面板里点 Flutter 网页版，截图总落后一帧、换屏后第一下点击常被吞 → 原型加网址参数直达各屏（`?stage=inGame&section=cards`），截图用本机 Chrome `--headless=new --screenshot --virtual-time-budget`，见 `apps/tablet/tool/shots.sh`。
- 原型界面的颜色对比度靠人看会漏（半透明压暗、夜读矩阵）→ 每对文字/底色写进 `Palette.textPairs`，夜读矩阵和界面共用常量，由 `flutter test` 算出并卡 4.5。
- Git Bash 里 `flutter build web --base-href /app/` 报「--base-href should start and end with /」→ MSYS 把 `/app/` 改写成了 `D:/…/Git/app/` → 这条命令在 PowerShell 里跑（或设 `MSYS_NO_PATHCONV=1`）。
- WPF 按钮写 `FontFamily="Segoe MDL2 Assets" Content="&#xE720;"` 却显示成方框 → `Shared.xaml` 的隐式 TextBlock 样式把字体盖回雅黑 → 图标一律写成 `<TextBlock Style="{StaticResource Icon}" Text="…"/>`。
- `<Trigger Property="Tag" Value="True">` 对绑定来的 bool 永远不触发 → Tag 是 object，"True" 被当字符串比较 → 用 `<Trigger.Value><sys:Boolean>True</sys:Boolean></Trigger.Value>`。
- 自绘 ComboBox 模板后可编辑下拉框（设置页模型 ID）打不了字 → 模板里少了 `PART_EditableTextBox` → 补上并按 `IsEditable` 切换显示。
- UI 测试截图只渲染窗口内容：控件本身透明底时截出来是白底，看着像配色坏了 → 截图前给被测控件设 `Background`，真实软件里它铺在主窗口底色上。
