# Prepared document and script package contract

## Document extraction envelope

应用自己的 formatVersion=1 包包含 manifest、toolIdentity、extractionBatches、completion、sourceIndex。
manifest 含每个文件的 fileId/hash/pageCount/order，保留原文件私有引用。
toolIdentity 含实际工具和版本、质量档位以及本地/远程处理记录；不存 key。
extractionBatches 保存每次 parse/read 的完整已验证内容、ranges 和 continuation 关联。
completion 仅在所有输入完整处理、无待继续请求且覆盖核对通过时为 Complete。
sourceIndex 将规范 SourceSpan 映射到 hash、真实页、块/locator；图片各自 page=1，排序独立。

## Accepting external prepared output

只能接受 formatVersion=1、源 hash 匹配、Complete、页覆盖完整且关键事实有来源的包。
只有 Markdown、页码无法关联源文件、内容丢块、版本不兼容或含未知字段类型时拒绝并给脱敏原因。
既有工具产物必须通过显式转换及来源核对才成为该格式，不能重命名扩展名冒充完整包。
导入预解析包不是 MinerU 失败后的隐式换解析器，也不构成远程资料授权。

## Frozen script package

验证后的 ScriptPackage 具有独立 schemaVersion=1、packageId/version/manifestHash、CanonicalSegment、phases/materials/roles 及私有 truth 段。
KeyFacts 的 sourceSpanIds 全部存在，未知事实保留 NeedsReview，绝不由模型填默认凶手。
入库注册前完整写入、校验、冻结；原始/私有文件不进入 UI 或发布包。
游戏开始仅加载已注册 Ready 版本，不调用文档工具。文件被改动导致指纹失配时不能继续该局。

## Public readiness

只返回公共标题、人数、文件/页覆盖、脱敏问题代码及 canStart。
不返回 truth、murderer、privateStory、原始 secret span、原始路径或未清洗的工具 stderr。
工具层 Complete 与应用层 Ready 必须分开，不声称结构校验自动证明语义保真。

## Tablet preparation extension

导入向导在识别完成后、最终建包前增加「平板材料准备」区，不另建一套导入流程。
每张卡建立稳定 cardId、displayName、groupName、sourceOrder、frontAssetId 与可选 backAssetId；
名称优先取自卡背已有名称或卡面明确印刷的标题/卡号，不让模型从正文概括剧情式名称；
仍缺名称显示来源编号并标「待命名」，编辑后才供平板分发。
卡片全部按名称列出，点哪张给哪张，没有按环节解锁。

整版使用通用网格参数（行数、列数、有效区域、有效槽位、正背面槽位映射），
不为任一剧本写专属尺寸、别名或匹配分支。记录原文件 hash、页号、有效区域、网格、槽位与映射以回溯每张派生单卡。
拆分质量未确认、配对不可靠或组资产缺失时该组 NotReady，暂停该组数字分发，请求返回 409，不以整页图片替代单张；取消导入只清理本次未发布临时资产，成功后原子发布清单与文件。
角色本原文排版：PDF 和图片来源均保留已提取的原文章节、块顺序、插图与源页映射，网页重新分页，不改写或补写。保留原稿用于本人核对；原生 PDF 解析与渲染按原稿核对需要使用，不是正文显示的主路径。识别完成、页覆盖完整和文字准确分别验证；密码保护、损坏或来源失配明确报告。
