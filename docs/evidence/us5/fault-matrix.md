# US5 故障矩阵：实测记录

更新日期 2026-10-19。全部为可自动化验证的行为，对应 spec 的 FR-010 / FR-011 与 SC-004。

## 结论

| 故障 | 期望 | 结论 |
|---|---|---|
| 模型断线（Magpie 不可达） | 显示脱敏错误、已提交进度不变、不播音 | **已通过** |
| TTS 故障（无中文音色） | 保留公开文字、明确错误码、不影响存档 | **已通过** |
| 同一操作重复 10 次 | 只提交一次 | **已通过** |
| 事务写入中途崩溃 | 三者（event / receipt / 投影）均不改变 | **已通过** |
| 提交后强退并重启 | 恢复最后完整状态，不自动播音 | **已通过** |
| 剧本文件被改动 | 拒绝恢复（PACKAGE_MISMATCH） | **已通过** |

## 命令

```powershell
dotnet test tests\LocalDm.Tests\LocalDm.Tests.csproj --filter "FullyQualifiedName~FaultMatrixTests|FullyQualifiedName~IdempotencyTests|FullyQualifiedName~RecoveryTests|FullyQualifiedName~SessionStoreTests"
```

结果：`FaultMatrixTests` 4 项通过；相关恢复/幂等/事务用例见 `flow-recovery.md`。

## 用例对照

| 测试 | 覆盖点 |
|---|---|
| `Model_offline_does_not_change_committed_progress` | 模型失败后 `revision` 与 `state` 不变，TTS 未播放 |
| `Tts_failure_keeps_text_and_does_not_touch_progress` | `TTS_UNAVAILABLE` + 「文字已保留」，事件数不变 |
| `Hard_exit_after_commit_restores_last_committed_state_without_auto_speech` | 重启后 revision=3，`SpeechState=Idle`，0 次自动播放 |
| `Crash_during_commit_never_loses_already_committed_history` | 事件写入后崩溃 → 回滚，历史只有 1 条 |
| `Ten_retries_with_same_operation_id_commit_exactly_once` | 10 次重试，1 条事件，revision=1 |
| `Changed_source_file_blocks_recovery` | 源文件改动 → `PACKAGE_MISMATCH` |

## 限制

- 这些是进程内故障注入，不是真的拔网线 / 杀进程；「强退」通过清空 SQLite 连接池并重新打开数据库模拟。
- OpenLess 退出对应用无影响（应用不依赖它），因此未单独做故障用例。
- 没有在真实 4–6 人整局中触发这些故障。
