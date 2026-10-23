VERDICT: PASS

- [P2] tests/LocalDm.Tests/TabletSpeechTests.cs:434 — A failed HTTP assertion leaves the web host running. `await app.StopAsync(ct);` executes only on success; the `finally` at line 436 only deletes the directory. Move host shutdown and disposal into `finally`, using a cleanup token unaffected by test cancellation.
- [P2] src/LocalDm.Tablet/TabletSpeechService.cs:104 — Added bloat: duplicated quota rollback logic. Both exception and rejected-outcome paths repeat `_last[sessionKey] = previous.Value` / `_last.Remove(sessionKey)`. Confirmed by `rg` at lines 108/112 and 125/129, covering every occurrence. Consolidate these into one rollback block.

Pre-existing problems: none identified in the reviewed scope.