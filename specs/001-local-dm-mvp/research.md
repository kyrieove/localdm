# Phase 0 research

所有决策均以单电脑范围为前提。研究只读本机帮助、安装源码和官方文档；未启动应用/解析服务，未解析真实资料，未安装依赖。
已有合成 Magpie/TTS 证据保存在 docs/evidence/p0 和 docs/preview，不能代替端到端验收。

## D1 Windows 原生软件

Decision：WPF、C# 14、net10.0-windows，win-x64 自包含发布文件夹，再用 per-user Windows 安装器包装，并提供同一发布文件夹的便携 ZIP。
Rationale：用户只需 Windows 成型软件；自包含不要求目标用户安装开发 SDK 或 .NET 运行时。
Alternatives：Electron 增加 Chromium/桥接，本地网页不满足独立软件目标；单文件发布并不省去原生组件验证，因此先用普通自包含目录。
来源：[WPF SDK](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props-desktop)、[自包含部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/)。
实施验证：当前未发现 SDK 版本；T001 准备构建环境，发行依赖版本必须稳定并锁定；签名与 SmartScreen 状态如实报告，不要求绕过安全提示。

## D2 普通模型与 Windows TTS

Decision：HttpClient 调用配置的 Windows Magpie loopback /v1/models 和非流式 /v1/chat/completions；默认选择已验证的 group/auto-deepseek-v4-1-flash，可另选实际列表模型。TTS 使用 System.Speech 和已安装中文音色。
Rationale：用户明确普通文字模型加 TTS，不要求音频模型。DeepSeek 合成输入已返回「连接成功」，Sol 只验证列表存在。
Alternatives：不绕开 Magpie 直连供应商、不使用 WSL、不引入实时语音或另一个云 TTS。
认证：本次合成请求未带认证头成功；成品允许配置 caller key 并用 DPAPI 保存，不能读取 Magpie 上游凭据。
来源：docs/evidence/p0/magpie-text-probe.json；[SpeechSynthesizer](https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer)、[取消播音](https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer.speakasynccancelall)。
实施验证：实际播音、音色缺失、停止与重播仍须目标机操作；不能把 WAV 合成当作真实扬声器验收。

## D3 OpenLess 只作为文字输入

Decision：原生 TextBox 接受 OpenLess 写入或用户粘贴；保留手动输入和发送按钮，IME 组合态不处理 Enter 为发送。
Rationale：不增加麦克风、ASR、快捷键注入或 OpenLess API；不能根据文字输入暂停自动发送。
Alternatives：自建录音或 ASR 重复现有功能；定时推断转写完成会丢长句或误操作。
来源：本机 OpenLess 进程与安装项；[WPF 输入](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/input-overview)、[PreviewTextInput](https://learn.microsoft.com/en-us/dotnet/api/system.windows.contentelement.previewtextinput)、[OpenLess 上游](https://github.com/Open-Less/openless)。
边界：上游文档不能证明本机模式或离线隐私；实际注入、IME 和软件焦点属于 US4 实机任务。

## D4 幂等与原子存档

Decision：Microsoft.Data.Sqlite 单文件、单写入者；operation receipt、event 和 session projection 在同一事务中更新。
Rationale：早期「事件文件 + 快照文件」需要跨文件恢复协议。SQLite 无数据库服务器，仍符合单电脑简单部署。
Alternatives：只有一个 JSON 快照会限制持久审核历史；独立数据库服务无必要。
来源：[SQLite 事务](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)、[原生依赖](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)。
实施验证：强退的前后状态必须落在完整提交之一，唯一 operationId 和 revision 在事务内校验。

## D5 MinerU 程序契约与全文

Decision：首版仅适配已定位 MinerU 4.0.1，ProcessStartInfo.ArgumentList 调外部进程并异步读 stdout/stderr，不经 shell。
Rationale：CLI 的 --json 是公开机读边界，内部缓存不是稳定格式；pdf-inspector 未定位，不能作为必备依赖。
PDF 首次 parse 必须显式 --pages all；该版本默认只处理前 10 页。图片不带 PDF 专用 --pages。
正常结果为 parse 与 content；content 可能为 null，退出码 0 不代表已完成。
完成必须 parse.status=done、content 对象非 null、无顶层 error，且完整处理所有 next_request；不要只判断 truncated。
parse 续读以 next_request.page_range/after 构造下一次 parse；read 续读使用 next_request.locator。保存每批 content_ranges.start/end 与页块 locator。
parse_wait_timeout 可仍为 pending/parsing，应保留任务并按原参数继续，不加 --force。
Alternatives：--output Markdown 虽有页标记，但缺块级 ranges；直接读取 doclib 私有缓存或失败静默换 OCR 均不采用。
来源：本机 mineru version、parse/read --help、server status 及安装源码；技能 C:/Users/ASUS/.codex/skills/mineru/SKILL.md。
实施验证：服务当前未运行，不能宣称任何引擎/OCR 已通过；合成文本 PDF、扫描 PDF、图片与超过 10 页的输入分别验证。

## D6 文档处理的隐私边界

Decision：应用专属 MINERU_HOME，隔离规则与服务；不复制用户全局规则或密钥，不自动启用远程。启动前确认 profile 本地引擎、解析规则和遥测状态，状态不明时不提交材料。
Rationale：省略 --remote 并不阻止已有 remote=true parsing rule；4.0.1 的遥测 unset 也不能当作 disabled。
Alternatives：盲用全局配置会继承未知外发；静默修改用户全局设置不符合范围。
若应用 profile 无可用引擎，提供明确恢复选项或导入用户已准备的版本化产物；不能假装本地解析成功。
远程真实资料处理必须另有绑定数据及目的地的明确授权；profile 配置不构成授权。
本轮不创建 profile、不改遥测、不启动服务；实施任务负责在用户选择后设置 app-owned profile，并记录脱敏状态。
来源：本机 4.0.1 客户端/ingest/parsing rule/telemetry 代码只读核查及 MinerU 技能。
补充核验：仅支持已核验的 4.0.1；app-owned profile 调用清除继承的 MINERU_CONFIG 和其他 MINERU_* 覆盖，再设置自己的 MINERU_HOME。
公开关闭遥测顺序为同 profile 的 server start、telemetry disable --json、telemetry status --json，解析真实材料前必须 disabled；本轮未执行这些变更。
locator 可能含 page、block 和 char，内容对象非 null 与文本非空是两回事，图片同样需要完整续读。

## D7 可验证公开口播

Decision：固定主持优先用原文，动态回答只从当前公开 CanonicalSegment 选择可引用片段和有限过渡句；不确定则诚实回应。
Rationale：普通文本模型不是可靠的秘密隔离或事实判定器，不把全剧本/真相送入普通回答再期待自审。
Alternatives：全知 prompt 或任意改写后由另一个 LLM 判断安全，不能提供确定的边界。
语气可通过已批准过渡句、播音节奏和模板调整；不牺牲原本事实换自由创作。

## D8 设计默认和未完成验证

纸笔投票汇总为当前最小默认，开局时显式接受且剧本允许；否则标记不支持，不创建电子秘密投票系统。
远程资料授权、真实剧本逐项核验、OpenLess 注入和 Windows 发布物均是执行门禁，不是靠研究推断通过。
设计没有未决协议选择；实际工具不可用时按既定失败/恢复契约停止，而不是现场改变范围。
