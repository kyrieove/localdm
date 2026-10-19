# US2 真实剧本准备：受阻记录

更新日期 2026-10-19。本文件不含任何真实剧本正文、角色秘密或真相。
完整的技术细节见 [tool-parse.md](tool-parse.md)。

## 结论

| 场景 | 结论 |
|---|---|
| 应用层导入管线（预检 → 提取 → 来源归一化 → 校验 → 冻结） | **已实现并通过自动化** |
| 合成 12 页文本 PDF 经 MinerU 提取 | **受阻：quality_tier_unavailable** |
| 扫描 PDF / 图片 OCR | **未执行**（同一引擎阻塞） |
| 真实剧本（`stories/<剧本目录>`，5 人）准备为 Ready 包 | **受阻** |
| 独立校验者逐项核对页/角色/阶段/线索/真相来源 | **未执行**（无 Ready 包可核） |

## 真实剧本形态

用户提供的 `stories/<剧本目录>/`：

- `<角色本目录>/`：6 个角色本目录，每个 12 张 JPG；
- `线索卡牌/`：10 个目录，共 208 张 JPG；
- 没有 PDF，**全部是图片页 → 全部依赖 OCR**。

## 为什么受阻

```powershell
$env:MINERU_HOME = "D:\2\dev\dm\.appdata\mineru-home"
mineru server start
mineru telemetry disable --json   # → state: disabled
mineru parse "fixtures\synthetic\text-12pages.pdf" --pages all --json
```

```json
{"error":{"type":"engine_error","code":"quality_tier_unavailable",
 "message":"No basic, standard, or advanced engine available. You can start a local parse-server or explicitly pass --tier flash for text-only preview."}}
```

本机 MinerU 4.0.1 已安装、服务可用、遥测已关闭，但**没有可用的 basic / standard / advanced 解析引擎**。
按契约，应用不静默换解析器、不自动加 `--tier flash`、不自动 `--remote`，因此返回 `TOOL_NOT_READY` 并显示恢复选项。

## 已通过的自动化覆盖（`ImportCoverageTests`，11 项）

- 全页覆盖 → `Ready` 且 `CanStart=true`；
- 缺页 / 待续读 / 工具未完成 → `CanStart=false`；
- 未确认关键事实 → `KEYFACT_NEEDS_REVIEW`，**不生成默认凶手**；
- 无来源段落 → `MISSING_SOURCE`；
- 公共报告不含 `.private/expected.json` 的秘密短语与私有路径；
- Markdown 冒充完整包 / 错误 formatVersion / 未完成包 / 缺页包 / 白名单外 locator 全部拒绝；
- 真实材料无授权时在调用工具**之前**就返回 `CONSENT_REQUIRED`。

## 恢复路径（需要用户决定，本轮未执行）

1. 在应用专属 profile 中配置本地解析引擎（basic / standard / advanced），重跑 `mineru parse`；
2. 仅用 `--tier flash` 做文本预览（无版面/OCR 质量保证，不能直接当 Ready）；
3. 提供已准备好的 `formatVersion=1` 完整应用包，走「接受外部产物」路径。

## 明确没有做的事

- 没有打开或解读任何角色本图片内容；
- 没有推断凶手或补写缺失事实；
- 没有用合成样例替代真实剧本；
- 没有把真实剧本任何内容发送到任何外部服务。
