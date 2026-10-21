# 原生 PDF 页数与渲染探针、二维码依赖核实

日期：2026-10-21。依据 `docs/plans/2026-10-21-tablet-distribution.md` 第 3 节第一步与 4.3 节。
材料：合成 PDF 在临时目录现造；未读取、未打印、未提交 `stories/`、`.appdata/`、`.local/` 下任何文件。

## 1. 现有字符串页数计数的边界（已实测）

`ScriptImportService.CountPdfPages` 只数未压缩的 `/Type /Page` 对象头。用对象流（ObjStm + FlateDecode）压缩的同一份 3 页合成 PDF，它只数出 **1**。

用例：`tests/LocalDm.Tests/WindowsPdfSpikeTests.cs::String_page_counting_used_today_misses_compressed_pdfs`（通过）。

结论：证实方案 4.3 的判断——现有计数不能作为角色本页数的最终依据，必须替换为原生解析。

## 2. Windows 原生 PDF 路线（未接入）

`Windows.Data.Pdf` 需要 Windows SDK 目标与 WinRT 投影，本仓库当前三个工程都没有接入。探针用例如实断言「不可用」并记录原因，没有伪造页数、没有静默退回字符串计数：

- `WindowsPdfSpikeTests::Native_pdf_page_count_is_not_wired_yet_and_reports_the_reason`（通过）
- `WindowsPdfSpikeTests::Native_pdf_render_is_not_wired_yet_and_reports_the_reason`（通过）

待落地时需要实际验证并记录：页数读取、逐页渲染字节、150 DPI 参数、发布物是否自带依赖（共享框架已含 `Microsoft.AspNetCore.App`，WinRT 投影情况需实测）。

## 3. 二维码生成依赖（本机离线不可用）

本机 NuGet 缓存（35 个目录）没有任何 QR 相关包：无 QRCoder、Net.Codecrete.QrCodeGenerator、ZXing、System.Drawing.Common、ImageSharp、SkiaSharp。离线模拟下 restore 报 `NU1101: 找不到包 QRCoder`。

结论：二维码库尚未锁定。实施第三步前需要：联网锁定库与版本（并核查许可写入 `docs/decisions` 或本文件），或自行实现 QR 编码（无第三方依赖，可行）。本轮不安装、不锁定。

## 4. 构建与测试

```
dotnet build LocalDm.slnx          → 0 警告 0 错误
dotnet test tests/LocalDm.Tests    → 304 总计 / 298 通过 / 6 跳过（EdgeTtsTests 已授权）/ 0 失败
dotnet test tests/LocalDm.UiTests  → 33 通过 / 0 失败
```
