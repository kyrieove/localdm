# 真实剧本解析：OCR 后端对比与实测结果

更新日期 2026-10-19。本文件不包含任何剧本正文、角色秘密或真相。

## 一句话结论

**MinerU 4.0.1 的 `flash` 本地档在这批图片剧本上明显优于轻量库，已接入应用并通过整本 12 页实测。**

- MinerU `basic/standard/advanced` 需要 parse-server，本机没有 → 不走。
- 轻量库实测都不够：「Windows 自带 OCR」几乎读不出竖排正文；「RapidOCR」可用但行序反了。
- `flash` 关键词命中 9/9、读序 4/4，且每页都保留页块来源。

## 三后端同页对比

测试页：`stories/<剧本目录>/<角色本目录>/唐/1.jpg`（正文为**竖排**，这是难点）。

| 后端 | 耗时 | 字符 | 关键词命中 | 读序正确 | 备注 |
|---|---|---|---|---|---|
| **MinerU flash** | 1.41 s | 65 | **9/9** | **4/4** | 竖排正确成行 |
| RapidOCR（ONNX） | 1.51 s | 57 | 7/9 | 2/4 | 字对，但行序反向 |
| Windows.Media.Ocr（zh-Hans-CN） | 0.26 s | 59 | 1/9 | 0/4 | 竖排被打散成乱序字 |

复现：

```powershell
python scripts\compare-ocr.py
```

原始数据：`docs/evidence/us2/ocr-comparison/comparison.json`。

### 各自的失败形态（实际输出）

RapidOCR：

```text
剧本标题（已隐去）
角色身份（已隐去）
学生。
……（中间各行为同一段介绍文字的倒序片段，已隐去）
角色（已隐去）
```

竖排从右往左读，所以正文 4 行是倒序；单行内基本正确。

Windows OCR：

```text
生 大 院 女 大 学 生 学 翰 休 色 女 。 学 聪 长 一 学 一 举 岁 圣 止 的 约 得 黑 ' 的 一
```

按横排扫描竖排文本，行内顺序全乱，不可用。

## 接入应用的实测（flash）

应用管线：隐私预检 → 授权 → 提取 → 页块来源归一化 → 工具层完成判定。

```powershell
dotnet run --project tools\LocalDm.Probe -- --import `
  --story "stories\<剧本目录>\<角色本目录>\唐" --pages 12 --tier Flash
```

实际结果（`docs/evidence/us2/import-probe.json`）：

- 工具版本 `4.0.1`，遥测 `disabled`，隐私预检通过；
- 授权范围 `LocalProcessingOnly`，绑定本批清单 hash；
- **12/12 页 `Complete`**，总耗时 87.5 秒，单页 5.7–9.9 秒；
- 每页 1 个页块来源，字符数 65–1312，locator 形如 `doc:<id>/tier:flash/page:N/block:0`。

## 已修复的两个真实缺陷

实测暴露了实现问题，两处都已修并回归：

1. **页块来源全部丢失**：原实现只读 `content_ranges[].text`，而 MinerU 4.0.1 的 CLI JSON 在该字段里**不含正文**，
   正文在 `content.content`（markdown）。结果 `ToolComplete=true` 但 `spans=0`，等于「成功但没有来源」。
   现改为按正文里的 `<!-- page N of M -->` 页标记切分，locator 取自 `content_ranges` 的页范围。
2. **`content_ranges` 的 `start/end` 是 locator 字符串**，原代码按 int 读取会抛异常。现两者都兼容：
   数字偏移写 `StartOffset`，locator 原值保留在 `StartLocator`。

## 仍然存在的限制（不要当成已解决）

- `flash` 是轻量档：合成样例里出现过把冒号写成 `:`、个别标点（`得体”聪慧`）错误的情况。**这不影响来源可追溯**，但影响正文可用性。
- 目前 `spans = 1/页`（整页一块）。块级定位（`block:N`）在 CLI JSON 里没有正文对应，故 locator 用 `block:0`，
  页级定位是准确的，块级精度未达到。
- 只实测了其中一个角色本的 12 页；其余 5 本角色本角色本与 208 张线索卡**尚未跑**。
- 图形/线索卡多为图片页面，OCR 只能拿到文字，图面信息（标记、连线）不在文本里。

## 下一步

1. 把这一本走完整的 `ScriptPackageBuilder` + `ReadinessReport`，让 `canStart` 真正翻成 true；
2. 其余 5 本角色本与线索卡按同一管线批量跑（约 6 分钟/12 页，全量约 25–30 分钟）；
3. 人工抽查每本首末页与真相页，确认没有把秘密和公开内容混层。
