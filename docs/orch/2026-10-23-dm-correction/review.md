VERDICT: FAIL

- [P1] src/LocalDm.App/ViewModels/HostViewModel.cs:1396 — An unavailable proposal leaves the previous confirmation active. For example, leave a valid revert proposal pending, then receive a vote-correction proposal outside a voting phase. The rejection hint appears, but “确认” still executes the old revert. This violates the card’s requirement that unavailable proposals show no confirmation bar. Fix: call `SetPendingCorrection(null)` in the rejection branch. Confirming code: `AppendLocalHint("DM 提议的改正现在不能做，请直接在左侧操作。");` followed only by property notifications. Scoped `rg` found one occurrence of this rejection-branch bug.

Bloat: none identified.  
Pre-existing problems: none identified.