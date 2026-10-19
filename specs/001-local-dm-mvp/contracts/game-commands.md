# Game command contract

桌面进程内契约，无 HTTP 端点。所有外部输入先验证，再交 GameEngine。

## Dispatch

输入为 sessionId、operationId、expectedRevision、type、typed payload。
返回 Committed(receipt, publicProjection)、Duplicate(originalReceipt)、Rejected(code, publicReason)。
相同 ID 不同 payload 返回 OPERATION_CONFLICT；过期 revision 返回 STALE_REVISION。

| 命令 | 前置条件 | 状态变化 |
|---|---|---|
| Start | Ready 冻结包、Prepared、groupReady | Playing，原本首阶段 |
| Advance | Playing、原本条件、groupReady | 恰好下一个允许阶段，释放相应编号提示一次 |
| ReleaseMaterials | 当前原本允许、材料 ID 存在且未释放 | 记录公开材料/编号，不显示私人内容 |
| SubmitVoteSummary | 原本投票阶段、confirmed、票数合法 | 记录唯一有效汇总；修订需新操作及原本明确许可 |
| Reveal | 原本条件、已提交合法投票结果或原本不要求、confirmed | Revealed，生成允许公开的真相投影 |
| Pause | Playing 或 Revealed | Paused，记录 resumeState，不改变 phase |
| Resume | Paused 且包指纹相同 | 恢复先前状态，不播音 |
| Complete | 原本允许且 Revealed | Completed |

所有提交遵守 data-model C04、C06–C09；输入失败不产生事件。
本地 SQLite 同一事务提交 event、receipt 和 session projection。异常统一映射为脱敏错误。

## Public projection

只返回 phase、revision、已公开段落/编号、允许动作及脱敏票数。未解锁时没有 truth 属性，而不是值为隐藏字符串。
主持窗口只拿投影，准备工具或私有包加载器不直接作为 UI data context。
ReplayAnnouncement 只引用已保存 safeText，与命令事务完全分开。

## Recovery

重启检查包指纹及数据库完整性；恢复最后完整事务。失配返回 PACKAGE_MISMATCH，不能换另一个包继续。
未提交操作不补执行，恢复后 SpeechState 为 Idle。
