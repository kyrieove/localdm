# External integration contracts

## Windows Magpie

Base URI 可配置，首版仅允许 loopback；实测默认 http://127.0.0.1:3425。
GET /v1/models 的 JSON data[] 中读取 id；200 列表不能替代生成测试。
POST /v1/chat/completions 请求含 model、messages、stream=false；普通输入为当前公开内容。
响应要求 choices[0].message.content 为非空文字。仅支持本项目验证的最小子集，不承诺 tools、全量结构化模式或流式兼容。
同一 contract 可在需要结构选择时要求 JSON 文本并在本机严格解析；无法通过模式校验则拒绝生成，不把无效内容直接播出。
可选 Authorization caller key 仅从用户在本软件明确配置处取得，不从 Magpie 供应商配置抓取。
HttpClient 取消与 requestId 失效同时执行；旧结果不得进入 UI/TTS。
/health 在本机返回 404，不作为在线诊断的必备端点。

## Windows TTS

列出可用中文声音；SpeakAsync 播放已通过公开政策的 safeText。
Stop 取消当前及队列中的播音，清空待播项；Replay 复用已有 safeText。
没有中文音色或设备失败时保留公开文字，返回 TTS_UNAVAILABLE/FAILED，不改变 session。
自包含 .NET 发布不包含系统声音；缺失声音须明确报告。

音色归属由 TtsVoice.Engine 表达，不从音色标识的前缀推断；标识前缀只用于传递引擎专有参数。
朗读按固定降级链进行：所选引擎失败后依次尝试 Edge 神经音色、Windows 内置音色。
任一级失败都不得丢弃已生成的公开文字，也不得把引擎侧异常（依赖缺失、进程路径、显存报错）显示到公共屏。

## 本机 VoxCPM 音色

完全在本机合成，口播内容不出本机，不经过任何外部语音服务。
以常驻 python 进程承载模型：加载一次后复用，每句只付推理成本。
模型目录或 HuggingFace 仓库名由用户配置，不得写死；档位随机器的显存与内存而定。
依赖未装齐、模型缺失或进程起不来时，探测结果为不可用，该音色不进入音色列表，读写照常降级。
模型权重与参考音频属用户私有素材：只在本机使用，不入安装目录，不外发，不提交到仓库。

## OpenLess

没有应用 API 调用，只有原生 TextBox 及发送入口。
本软件不调用麦克风、不模拟 OpenLess 全局快捷键、不改它的隐私或模型设置。
输入插入与手动粘贴均可用；中文组合输入 Enter 不应误发。实机按用户已有工作方式验收。

## MinerU 4.0.1 CLI

使用明确的可执行绝对路径和 app-owned MINERU_HOME；不经 shell，不执行来自输出的命令字符串。
调用前核对 version 严格等于 4.0.1、server status、有效规则、引擎和隐私状态；未知即返回 TOOL_NOT_READY。
服务启动只针对经用户选择的应用 profile，不停止或修改全局用户服务。
版本支持锁定实际核验的 4.0.1，升级后须重新跑契约用例，不能默认兼容全部 4.x。
子进程环境先清除继承的 MINERU_CONFIG 及其他 MINERU_* 覆盖/密钥，再设置应用自己的 MINERU_HOME；不影响父进程或用户全局环境。
同一 profile 初次解析前按 server start → telemetry disable --json → telemetry status --json 的公开命令顺序设置并确认 disabled。
这些服务和设置操作仅针对用户选择的 app-owned profile；不直接写 MinerU 内部 SQLite，不在本轮执行。

PDF 命令参数为 parse、absoluteSourcePath、--pages、all、--json；图片为 parse、absoluteImagePath、--json。
不默认使用 flash 档，不猜可用 tier，不自动加 --remote/--force。
stdout 是 JSON，stderr 为告警；两个流异步消费，stderr 需脱敏后才显示。

预期正常 JSON 顶层为 parse、content；parse 含 status、sha256、short_id、tier、page_range 等。
content 可为 null；非空含 content、content_ranges、truncated、next_request、request_scope。
content_ranges 中保留 page_range、start、end，原始定位符格式为 doc:ID/tier:TIER/page:N/block:N。
定位符完整形态为 doc:ID/tier:TIER/page:N，后面可选 /block:N，再可选 /char:OFFSET；ranges.start/end 必须保留实际字符偏移，不能强行删成块号。
图片响应采用同一 parse/content 模型，也必须处理可能的 next_request；空白页的文本可为空，content 对象非 null 不等于内部文字必须非空。
退出码 0 且 parse.status=done、content 对象非 null、无 error、所有 continuation 完结，才结束提取任务；仍须应用的保真校验才能 Ready。

parse 继续请求只能读 next_request 的 page_range 与 after 字段并构造白名单参数。
read 继续请求只能读 next_request.locator 并用 read、locator、--json 构造参数。
两个路径保存内容范围，检测循环继续请求和遗漏页。next_request 非空时不能因 truncated=false 提前结束。
parse_wait_timeout 且 pending/parsing 视为等待中，允许按原参数继续，不丢任务、不强制重解析。
quality_tier_unavailable、no_engine、parse_server_unavailable、remote_not_allowed 显示实际可行恢复选项，不静默换解析器或外发。

导入用户准备的完整应用包使用 document-package.md，不读 MinerU 私有缓存，也不把 --output 的 Markdown 当作有块定位的完整包。
