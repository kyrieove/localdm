# 任务：平板分发第二步（一部分）——线索卡冻结资产与名称索引

先读 `docs/plans/2026-10-21-tablet-distribution.md` 第 1、4.1、4.2、5.1 节和第 6 节"第二步"，以及 `docs/decisions/` 里的 ADR 0005。

本次只做**线索卡**这一半，不做角色本（4.3）、不做导入向导界面、不做网页：
- 新建 `src/LocalDm.Core/Tablet/TabletManifest.cs`、`TabletQueries.cs`：平板清单、卡片名称目录（`cardId`、`displayName`、`groupName`、`sourceOrder`、`frontAssetId`、可选 `backAssetId`、组准备状态）和只读查询契约。明确不含任何环节解锁判断。
- 新建 `src/LocalDm.Infrastructure/Import/TabletAssetBuilder.cs`、`GridCardCropper.cs`：单卡配对候选、通用网格裁切（行、列、有效区域、有效槽位、正背面槽位映射、可选水平反转；每条边按整图位置独立取整）、资产 hash 与来源追溯。
- 在 `ScriptPackage` 中加入**可选**的平板清单并纳入 `ContentHash`；旧包没有清单时照常读写、纸质模式不受影响。
- 测试先写：新建 `tests/LocalDm.Tests/TabletAssetTests.cs`、`TabletQueryTests.cs`，用代码生成的合成图片，覆盖方案第 7 节表格里前三行和"拆分质量未确认或组资产缺失"那行中不涉及 HTTP 的部分（5×2、左半区 5×1、反转映射、边长不能整除、同名卡、改显示名、旧包兼容）。按实际图片像素标记断言，不能只断言没报错。

不要改 `src/LocalDm.Tablet/`、`src/LocalDm.App/`、`SessionStore.cs`、`SqliteSchemaSql.cs`（其他任务在改）。
不为任何一本剧本写专属尺寸、文件别名或匹配分支。
