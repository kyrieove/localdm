# US2 文档工具与真实剧本准备：实测记录

更新日期 2026-10-19。记录命令、结果与阻塞，不包含真实剧本正文或任何密钥。

## 场景与结论

| 场景 | 结论 | 说明 |
|---|---|---|
| MinerU 版本核对 | **已通过** | `MinerU 版本: 4.0.1`，Python 3.12.14 |
| 应用专属 profile 隔离 | **已通过** | `MINERU_HOME=D:\2\dev\dm\.appdata\mineru-home`，`server status` 的 `mineru_home` 与之相符 |
| 服务启动（仅应用 profile） | **已通过** | `mineru server start` 返回 `服务已启动(PID 31528)` |
| 遥测确认 disabled | **已通过** | `telemetry disable --json` → `state: disabled`；再查 `telemetry status` 仍为 `disabled` |
| 12 页文本 PDF 解析（`--pages all`，默认档） | **受阻：quality_tier_unavailable** | 见下；basic/standard/advanced 需要 parse-server |
| 12 页文本 PDF 解析（`--tier flash`，本地档） | **已通过** | 12/12 页，页标记完整 |
| 真实剧本单页（图片）`flash` 解析 | **已通过** | 页块来源与正文均产出 |
| 应用层覆盖校验与阻止开局 | **已通过（自动化）** | `tests/LocalDm.Tests/ImportCoverageTests.cs` 11 项 |
| 真实剧本单个角色本整本 12 页走应用管线 | **已通过** | 12/12 `Complete`，87.5 秒，见 [ocr-backend-comparison.md](ocr-backend-comparison.md) |
| 真实剧本其余 5 本角色本 + 208 张线索卡 | **未执行** | 管线已通，尚未批量跑 |
| OCR 后端选型（flash vs 轻量库） | **已通过** | MinerU flash 9/9 关键词、4/4 读序，优于 RapidOCR 与 Windows OCR |

## 命令与原始结果

### 1. 版本与服务

```powershell
$env:MINERU_HOME = "D:\2\dev\dm\.appdata\mineru-home"
mineru version
mineru server start
mineru telemetry disable --json
mineru telemetry status --json
```

结果摘要：

- `version` → `MinerU 版本: 4.0.1` / `Python 版本: 3.12.14`
- `server start` → `服务已启动(PID 31528)`，退出码 0
- `telemetry disable --json` → `"state": "disabled"`, `"executed": true`
- `telemetry status --json` → `"state": "disabled"`，`pending_metrics: 0`

### 2. 解析合成 12 页文本 PDF

```powershell
mineru parse "D:\2\dev\dm\fixtures\synthetic\text-12pages.pdf" --pages all --json
```

退出码 1，原始输出（`.raw.json` 同名文件）：

```json
{
  "error": {
    "type": "engine_error",
    "code": "quality_tier_unavailable",
    "message": "No basic, standard, or advanced engine available. You can start a local parse-server or explicitly pass --tier flash for text-only preview.",
    "param": null
  }
}
```

**结论：受阻。** 本机 MinerU 4.0.1 已安装且服务可用，但没有可用的 basic / standard / advanced 解析引擎。

按 `contracts/integrations.md`：

- 不静默换解析器；
- 不自动加 `--tier flash`（contract 明确「不默认使用 flash 档」）；
- 不自动加 `--remote`（无数据外发授权）。

因此应用在 `MineruPrivacyPreflight` 之后会返回 `TOOL_NOT_READY` 并显示可行恢复选项，而不是假装解析成功。

### 已采用的路径：`--tier flash`（本地档，用户明确选择）

`flash` 与 basic/standard/advanced 的执行方式不同：`flash` 在**本机**跑（不需要 parse-server），
另外三档都走 `_parse_via_api`（需要 parse-server 或远程）。本机没有 parse-server，所以只有 `flash` 可走。

实测 `flash` 之后，三后端同页对比的结论是 **flash 最优**（关键词 9/9、读序 4/4），
优于 RapidOCR（7/9、2/4）和 Windows 自带 OCR（1/9、0/4）。完整对比见
[ocr-backend-comparison.md](ocr-backend-comparison.md)。

契约因此调整为：**tier 由调用方显式指定，不自动降级**；`ParseTier.Flash` 默认值只在调用方主动选择时使用，
basic/standard/advanced 仍需 parse-server，不会自动启用远程。

### 若要更高档位（仍需用户决定）

1. 在应用专属 profile 中配置 parse-server（basic/standard/advanced），然后重跑；
2. 提供已准备好的 `formatVersion=1` 完整应用包，走「接受外部准备产物」路径。

## 真实剧本（stories/<剧本目录>）

用户提供的真实剧本形态：

- `<角色本目录>/` 下 6 个角色本目录，每个 12 张 JPG，共 72 页；
- `线索卡牌/` 下 10 个目录，共 208 张 JPG；
- 没有 PDF，全部是图片页 → 全部依赖 OCR。

**当前结论：管线已通，单个角色本整本 12 页实测通过。**

```powershell
dotnet run --project tools\LocalDm.Probe -- --import `
  --story "stories\<剧本目录>\<角色本目录>\唐" --pages 12 --tier Flash
```

- 授权：用户已显式授权「真实剧本用本地 flash 解析」，范围 `LocalProcessingOnly`，绑定本批清单 hash；
- 结果：12/12 页 `Complete`，总耗时 87.5 秒，每页均有页块来源与正文；
- 原始数据：`docs/evidence/us2/import-probe.json`。

**仍未完成：** 其余 5 本角色本角色本与 208 张线索卡尚未批量跑，因此整本剧本**还不是 Ready 包**。
未做且不得声称的事：没有推断凶手，没有把秘密与公开内容混层，没有用合成样例替代真实剧本。

## 自动化覆盖（已实际运行）

```powershell
dotnet test tests\LocalDm.Tests\LocalDm.Tests.csproj --filter "FullyQualifiedName~ImportCoverageTests"
```

结果：`总计: 11  失败: 0  成功: 11`。

覆盖内容：

- 全页覆盖 → Ready 且 `CanStart=true`；
- 缺页 / 未完成续读 / 工具未完成 → `CanStart=false`；
- 未确认关键事实 → `KEYFACT_NEEDS_REVIEW`，不生成默认凶手；
- 无来源段落 → `MISSING_SOURCE`；
- 公共报告不含 `.private/expected.json` 里的秘密短语与私有路径；
- Markdown 不能冒充完整包、错误 formatVersion、未完成包、缺页包、白名单外 locator 全部被拒绝。

## 限制

- 引擎阻塞不是应用层缺陷，但确实意味着本机**尚不能**完成真实剧本准备。
- 扫码 PDF 与图片路径只有自动化规则测试，没有真实 OCR 结果。
- 合成 fixture 的 known-truth 只用于断言，不构成真实剧本验收。
