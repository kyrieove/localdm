# 原生 PDF 页数与渲染探针、二维码依赖核实

日期：2026-10-21。依据 `docs/plans/2026-10-21-tablet-distribution.md` 第 3 节第一步与 4.3 节。
材料：合成 PDF 在临时目录现造；未读取、未打印、未提交 `stories/`、`.appdata/`、`.local/` 下任何文件。

## 1. 原生 PDF 路线：已实际接入并通过

测试工程目标改为 `net10.0-windows10.0.26100.0`，取得 WinRT 投影后真实调用：

| 调用 | 用途 |
|---|---|
| `PdfDocument.LoadFromFileAsync` | 打开文件 |
| `PdfDocument.PageCount` | 读真实页数（解析页树，不数字符串） |
| `PdfPage.RenderToStreamAsync` | 逐页渲染成位图流 |
| `BitmapDecoder.CreateAsync` + `GetSoftwareBitmapAsync` | 取回像素 |
| `SoftwareBitmap.CopyToBuffer` | 读进托管缓冲区 |

用例与结果（全部通过）：

- `Native_api_reads_the_real_page_count_of_a_synthetic_pdf`：3 页合成 PDF 读出 3。
- `Native_api_renders_each_page_to_a_decodable_bitmap`：每页渲染出 300x450 可解码位图，且不是纯色空图。
- `Native_api_renders_drawn_content`：每页画一个位置随页号变化的实心黑块，渲染后黑块首行像素位置与按 MediaBox 推算的位置一致（±12 像素，含抗锯齿边）。
- `Object_stream_pdf_is_readable_but_windows_pdf_cannot_open_it`：合成的对象流样本缺 xref，`Windows.Data.Pdf` 打不开，如实记录 `COMException`。真实剧本包不会是这种畸形结构；这条不构成「原生路线不可用」的结论。

## 2. 现有字符串页数计数的边界（已实测）

`ScriptImportService.CountPdfPages` 只数未压缩的 `/Type /Page` 对象头。

| 样本 | 字符串计数 | 原生 PageCount |
|---|---|---|
| 明文页对象的 3 页 PDF | 3 | 3 |
| 页对象收进 ObjStm + FlateDecode 的 3 页 PDF | **1** | 打不开（样本缺 xref） |

用例：`String_page_counting_used_today_misses_object_streams`（通过）。
结论：证实方案 4.3 的判断——现有计数不能作为角色本页数的最终依据，必须替换为原生解析。
注意：本轮未构造「对象流 + 完整 xref」的合法样本来对比原生计数，标记为后续补充项。

## 3. 二维码生成依赖（本机离线不可用）

本机 NuGet 缓存没有任何 QR 相关包：无 QRCoder、Net.Codecrete.QrCodeGenerator、ZXing、System.Drawing.Common、ImageSharp、SkiaSharp。离线模拟下 restore 报 `NU1101: 找不到包 QRCoder`。

结论：二维码库尚未锁定。实施第三步前需要：联网锁定库与版本（并核查许可写入 `docs/decisions` 或本文件），或自行实现 QR 编码（无第三方依赖，可行）。本轮不安装、不锁定。

## 4. 构建与测试

```
dotnet build LocalDm.slnx          -> 0 警告 0 错误
dotnet test tests/LocalDm.Tests    -> 312 总计 / 306 通过 / 6 已授权跳过 / 0 失败
dotnet test tests/LocalDm.UiTests  -> 33 通过 / 0 失败
```
