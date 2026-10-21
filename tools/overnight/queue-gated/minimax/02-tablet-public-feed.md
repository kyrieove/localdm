# 任务：平板分发第四步（一部分）——公开消息记录的存储层

先读 `docs/plans/2026-10-21-tablet-distribution.md` 第 5.2 节最后的 `GET /tablet/public` 和第 5.3 节。

本次只做**存储与核心类型**，不接 WPF、不做网页：
- 在 `src/LocalDm.Core` 新建不可变的公开消息类型：本局 ID、单调序号、作者、正文、时间、类型（玩家发言 / 主持回复 / 公开流程事件）。
- 在 `src/LocalDm.Infrastructure/Storage/SessionStore.cs`、`SqliteSchemaSql.cs` 新增公开消息表：追加、按 `after` 序号分页读取、按本局隔离；和 `dm_conversations` 分开存，不存备忘，不从 AI 备忘重建。旧数据库升级时能自动建表。
- 写入失败要抛出明确的异常类型，调用方以后据此显示"同步异常"，不能吞掉。
- 测试：新建 `tests/LocalDm.Tests/TabletPublicFeedTests.cs`，覆盖顺序与序号单调、重复追加去重（同一消息 ID 只存一次）、`after` 补发、不同局隔离、重启（新建 SessionStore 读同一文件）后还能读、旧库升级。

不要改 `HostViewModel.cs`、`ScriptPackage.cs`、`src/LocalDm.Tablet/`（其他任务在改或以后再接）。
