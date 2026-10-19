# US1 桌面口播：实测记录

更新日期 2026-10-19。本文件只包含合成公开文字，不含任何真实剧本内容或密钥。

## 结论

| 场景 | 结论 | 证据 |
|---|---|---|
| 本机 Magpie 模型列表 | **已通过** | `desktop-speech-probe.json`：`/v1/models` 200，56 个 model ID |
| 合成公开输入 → 模型文本 | **已通过** | 返回「欢迎五位侦探来到灯塔！请开始阅读第一幕。」 |
| System.Speech 中文播音 | **已通过** | 真实扬声器播放成功，耗时 10285 ms |
| 重播不请求模型 | **已通过** | 重播耗时 0 ms，文本与之前完全一致 |
| 停止/取消丢弃晚到结果 | **已通过（自动化）** | `SpeechLifecycleTests`：5 项 |
| 无中文音色 / TTS 失败保留文字 | **已通过（自动化）** | `TtsServiceTests` + `SpeechLifecycleTests` |
| 真实窗口界面（导航/空状态/主持台） | **已通过** | `host-100.png`、`docs/evidence/us3/00-host-page.png` |
| 125% / 150% 缩放截图 | **未执行** | 见「限制」 |

## 命令

```powershell
# 合成口播探针（真实 Magpie + 真实 TTS，只发送合成文字）
dotnet run --project tools/LocalDm.Probe -- --speech --out docs/evidence/us1

# 自动化用例
dotnet test tests/LocalDm.Tests/LocalDm.Tests.csproj --filter "FullyQualifiedName~MagpieGatewayTests|FullyQualifiedName~SpeechLifecycleTests|FullyQualifiedName~TtsServiceTests"
```

## 实际结果

```json
{
  "modelId": "group/auto-deepseek-v4-1-flash",
  "modelsEndpoint": { "ok": true, "elapsedMs": 116, "modelCount": 56, "selectedPresent": true },
  "chineseVoices": ["Microsoft Huihui Desktop (zh-CN)", "Microsoft Huihui (zh-CN)", "Microsoft Kangkang (zh-CN)", "Microsoft Yaoyao (zh-CN)"],
  "speech": { "state": "Idle", "late": false, "elapsedMs": 10285, "text": "欢迎五位侦探来到灯塔！请开始阅读第一幕。" },
  "replay": { "state": "Idle", "late": false, "elapsedMs": 0, "text": "欢迎五位侦探来到灯塔！请开始阅读第一幕。" }
}
```

用例统计：`MagpieGatewayTests` 8 项、`SpeechLifecycleTests` 5 项、`TtsServiceTests` 4 项，全部通过。

## 界面

`host-100.png` 是真实 `LocalDm.exe` 窗口（100% 缩放）通过 PrintWindow 截取：导航、首页空状态、不自动请求模型。

## 限制与未验证项

- **125% / 150% 缩放未截图。** 当前会话无法在前台操作 GUI，改动系统缩放会改变用户桌面设置，因此没有执行。
  需要人工在「设置 → 显示 → 缩放」切换后打开 `LocalDm.exe` 复核布局。
- 扬声器可听性由本机实际播放确认（`elapsedMs` 与真实播音时长一致），但没有人工听音记录。
- Magpie 下游可能为远程模型；本次只发送合成公开文字，不含真实剧本。
- 本项证据不等于真人整局验收（见 US5）。
