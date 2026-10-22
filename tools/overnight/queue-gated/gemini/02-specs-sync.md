# 任务：平板分发实施前的规格同步（只改文档和校验脚本）

先读 `docs/plans/2026-10-21-tablet-distribution.md` 第 5、8 节，ADR 0005，`.specify/memory/constitution.md`。

方案第 8 节要求实施前更新规格。本次只改文档和校验脚本，不改任何 C# 代码：
- 更新 `specs/001-local-dm-mvp/spec.md`、`plan.md`、`research.md`、`data-model.md`、`tasks.md`，以及 `contracts/` 下相关契约（`tablet-distribution.md` 已存在，核对并补齐第 5 节的入口、错误码、会话与公开消息约定）。保持旧游戏状态规则不变。
- 方案原文是什么就写什么，不新增产品决定。特别注意：卡片全部按名称列出、点哪张给哪张、**没有按环节解锁**；平板浏览器不限定品牌。
- 更新 `scripts/validate-planning.ps1`：去掉硬编码的宪章 3.0.0 和旧任务数量，改为检查与当前宪章版本、当前 tasks.md 一致。不能改成无条件通过。运行它并确认通过。
- 不写任何剧本内容。

不要改 `src/`、`tests/` 下的任何文件。
