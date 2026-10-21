# Data model

本文为计划中的领域契约，尚无数据库或 C# 实现。ID 为不透明字符串，外部 JSON 在边界验证后才能构造领域对象。

## 共享约束

以下约束编号用于任务和测试引用；任务逐字引用其内容。

- C01「所有 ID 必须非空，SourceSpan 必须引用存在的文件与页块。」
- C02「每个输入文件的 hash 与页数必须记录，覆盖必须完整；关键字段必须具有非空 sourceSpanIds。」
- C03「只有 Ready 可以开局；NeedsReview、Failed、Cancelled 不可以开局。」
- C04「剧本 packageId、version、manifestHash 在一局内不可改变。」
- C05「公开投影不能包含秘密、未释放线索或未解锁真相。」
- C06「revision 从 0 开始，每次提交恰加 1；expectedRevision 必须等于当前 revision。」
- C07「operationId 在同一 session 内唯一；相同 ID 和相同内容返回原结果，不同内容返回冲突。」
- C08「阶段推进必须满足原本条件及 groupReady=true；揭密和票数提交必须 confirmed=true。」
- C09「票数必须为非负整数，汇总总数符合当前剧本的参与者与投票规则。」
- C10「每次请求有唯一 requestId；取消或被替代请求的输出不得显示或播音。」
- C11「同一时刻最多一段播音；重播不得生成事件、调用模型或重新提交操作。」
- C12「未知外发边界视为禁止；没有数据授权不得发送真实文档或真实剧本文字。」
- C13「平板取图必须按 cardId 与冻结资产索引解析，不得按显示名称拼路径或模糊命中取第一张。」
- C14「卡片组未准备好时请求必须失败并显示原因，不得以整页图片替代单张。」
- C15「角色页只能从 Cookie 认领角色解析，客户端不能指定他人角色或任意文件路径。」
- C16「公开消息必须带本局 ID 与单调序号，按序号去重；模型备忘不得进入投影。」
- C17「局域网服务默认关闭，只在用户开启后绑定所选局域网 IPv4；停止后旧令牌与旧会话立即失效。」

## ScriptPackage

字段 packageId、version、title、playerCount、manifestHash、sourceFiles、roles、phases、materials、truth、validation。
首版 playerCount 属于 4、5、6；此值不是软件内账号数量。
sourceFiles 是 SourceFile 列表，含 fileId、原始引用、hash、pageCount、解析状态及工具版本；原始路径仅私有存储。
roles 仅保存外部私人材料索引；私密正文存在准备/引擎私有数据，公共界面没有读取接口。
phases 含有序 phaseId、公开口播片段、允许动作、原本条件、材料包编号、投票/揭秘标志及 sourceSpanIds。
truth 单独位于包的私有段，PublicPackageProjection 不含此字段。
应用 C01、C02、C04、C05。准备完成后只读，变更生成新版本，不改已有局。

## TabletManifest（新增，可选）

字段 manifestVersion、assetRootFingerprint、roles、cards、groups、preparedAt。
旧包没有该段时继续支持纸质模式，界面提示重新准备后才能使用平板。
cards 每项含 cardId、displayName、groupName、sourceOrder、frontAssetId、可选 backAssetId、preparationState。
preparationState 为 Ready 或 NotReady；NotReady 必须带脱敏原因分类（缺正面、配对冲突、裁切未确认、资产缺失）。
名称是玩家定位入口，取图只用 cardId；同名卡的列表项必须显示分组和编号，禁止模糊搜索命中后自动取第一张。应用 C13、C14。
roles 每项含 roleId、RoleBookText 与有序原稿 assetId 列表；本人原文仅由角色接口提供，物理路径、组织者手册与 truth 不进入该段。

## TabletAsset（新增）

字段 assetId、相对路径、内容 hash、类型（CardFront/CardBack/RolePage）、来源文件与页号、网格槽位信息。
资产目录以包内容指纹隔离，客户端只经 assetId 间接引用，不接收物理路径。应用 C05、C13、C15。

## PublicTabletMessage（新增）

字段 sessionId、sequence、author、text、kind、committedAt。
由同一个 SessionStore 写入 SQLite，与模型记忆（DmConversationSnapshot）分别保存；备忘不进入该记录。
浏览器按 sessionId + sequence 去重，重连后按 after=sequence 补发。应用 C16。

## TabletSession（新增）

合成阅读探针已实现 RoleBookText、RoleBookChapter、RoleBookBlock、ReadingPosition 和 ReadingSettings。章节与块顺序保持原样，字符流包含一次章题、节题、正文和插图原有文字及图注；空分隔不加字符。偏移按 UTF-16 计数，0 为有效起点。原稿页号单独保留。正式冻结包接入与持久化尚未实现，当前内存中的合成会话不能当作完整存档。

字段 browserSessionId、sessionId（本局）、roleId、joinedAt。
由加入令牌换取，仅存 Cookie；不做设备绑定、账号或电脑审批。服务停止、切局或重启后失效。应用 C17。

## SourceSpan 与 CanonicalSegment

SourceSpan 字段 spanId、fileId、pageNumber、blockNumber、toolLocator、textHash、originalText。
pageNumber 为来源文件内的实际页序，单张图片记 pageNumber=1；排序由 manifest 明确，不以文件名字典序猜剧情。
CanonicalSegment 字段 segmentId、originalText、sourceSpanIds、visibility、releasePhaseId。
visibility 为 Public、Private、Truth，且由程序筛选，不能接受模型决定可见性。
关键事实 sourceSpanIds 不得为空；不存在的原文不进入规范事实。应用 C01、C02、C05。

## ImportJob 与 ReadinessReport

ImportJob 字段 jobId、sourceManifest、toolIdentity、privacyDecision、state、processedPages、continuationState、errors。
state 为 Created → PrivacyChecked → Parsing → Validating → Ready 或 NeedsReview/Failed/Cancelled。
PrivacyChecked 仅在确认有效工具规则及本地/远程边界后成立；工具未运行、无引擎或权限不明不得进入 Parsing。
continuationState 持有工具返回的下一请求，不执行自由 shell 命令字符串，只解析白名单 read 参数。
ReadinessReport 字段 publicTitle、playerCount、file/pageCoverage、issueCategories、canStart。
报告不能包含角色秘密、凶手名称、具体秘密引用片段或内部工具原始异常全文。
应用 C02、C03、C05、C12。工具返回完成和 canStart 不等价。

## Session、GameCommand、GameEvent

Session 字段 sessionId、packageId、packageVersion、manifestHash、state、currentPhaseId、revision、releasedMaterialIds、voteSummary。
state 为 Prepared、Playing、Paused、Revealed、Completed；Paused 另存 resumeState，不能覆盖揭秘状态。
GameCommand 字段 sessionId、operationId、expectedRevision、type、payload。
type 为 Start、Advance、ReleaseMaterials、SubmitVoteSummary、Reveal、Pause、Resume、Complete。
payload 仅含该命令需要的明确数据：groupReady、confirmed、票数或材料 ID，不含自由代码。
GameEvent 字段 eventId、sessionId、operationId、revision、type、safePayload、committedAt；safePayload 不拷贝完整真相。
同一事务插入 operation receipt 和 event，并更新 Session projection。事务失败时三者均不改变。
应用 C04、C06、C07、C08、C09。

## PublicHostRequest、PublicHostReply 与 SpeechState

PublicHostRequest 字段 requestId、sessionId、revision、inputText、allowedSegmentIds、intent。
intent 为 PublicQuestion、FixedAnnouncement 或 ProposeCommand；后者只能返回 UI 提案，不执行命令。
PublicHostReply 字段 requestId、referencedSegmentIds、mode、text；mode 为 ApprovedExcerpt、SafeTransition、Uncertain、Refuse。
首版以可验证的公开原文片段及白名单过渡句播报。任意改写无法确定依据时返回 Uncertain，而非声称已验证语义安全。
SpeechState 为 Idle、GeneratingText、Speaking、Cancelled、Error，不存在应用录音状态。
SpeechItem 字段 speechId、requestId、safeText、generationId；不把 replay 作为 GameCommand。
应用 C05、C10、C11。

## ToolSettings 与 DataConsent

ToolSettings 含 magpieBaseUri、modelId、optionalCallerKeyRef、mineruExecutable、voiceId、inputHint。
magpieBaseUri 首版仅允许 loopback HTTP/HTTPS；modelId 来自实际配置或列表，不按显示名称猜测。
optionalCallerKeyRef 指向 DPAPI 密文引用，原始 key 不序列化进普通设置和日志。
DataConsent 绑定目标工具、实际处理目的地、数据种类、source manifest hash 和允许范围，不用一次普通文本测试代替剧本外发授权。
OpenLess 作为外部文字输入，应用不读取或修改其录音/上游配置。
应用 C12。

## 本地存储关系

packages 的 packageId/version/manifestHash 对应只读剧本包。
sessions 引用 package，events 与 operation_receipts 引用 session；receipts 对 sessionId/operationId 建唯一约束。
session projection 与 events/receipts 在单 SQLite 事务中更新；单进程串行处理写操作。
prepared bundles 与原材料不在数据库事务中改写；新包先完整写入临时位置并验证，再注册引用，未完成包不能开局。
public_tablet_messages 引用 session，按 sessionId + sequence 唯一；与 dm_conversations 分开存储，互不读取。
