VERDICT: FAIL

- [P1] src/LocalDm.Infrastructure/Voice/Dictation/DictationService.cs:316 — `Dispose()` sets `_disposed = true` before calling `Cancel()` at line 319. `Cancel()` immediately returns at line 259, so disposing while listening leaves the microphone recording and `_pcm` accumulating audio. Fix by performing microphone/buffer cleanup before setting the disposal guard, or calling an internal cleanup method that bypasses it.

- [P1] src/LocalDm.Infrastructure/Voice/Dictation/DictationService.cs:275 — `Cancel()` unconditionally executes `SetState(DictationState.Idle)` even while recognition continues at line 228: `_recognizer.Transcribe(samples)`. A subsequent start is therefore accepted during recognition, bypassing the card’s busy check; the previous stop’s `finally` can also overwrite the new recording’s state. Fix by returning without changing state unless currently listening.

- [P1] src/LocalDm.Infrastructure/Voice/Dictation/DictationModelStore.cs:79 — Filesystem enumeration occurs outside the exception handler, so denied access throws instead of returning the required preparation-failure message. This exception also leaves `StartAsync()` stuck in `Preparing`, since its preparation block catches only cancellation. This class of unprotected enumeration occurs at lines **54 and 79**, confirmed with `rg`. Fix by including readiness/source validation inside the preparation exception boundary and restoring service state on preparation failure.

- [P1] src/LocalDm.Infrastructure/Voice/Dictation/DictationService.cs:247 — The one-shot idle callback releases the model only when `_state == DictationState.Idle`. If it fires during a later recording, it does nothing; cancelling that recording or stopping a short recording never rearms it. The loaded model can then remain indefinitely despite the five-minute release requirement. Confirming lines: timer scheduling at **225 and 232** uses `Timeout.InfiniteTimeSpan`; neither return-to-idle path rearms it. Fix by scheduling release when entering idle, or rearming after a skipped callback.

Bloat: none identified.

Pre-existing problems: none identified within the reviewed scope.