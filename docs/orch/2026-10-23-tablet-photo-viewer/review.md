VERDICT: FAIL

- [P1] src/LocalDm.Tablet/wwwroot/photo-viewer.js:396 — Zoomed navigation to an unloaded page can leave it incorrectly positioned, with blank space visible. `go()` calculates pan using fallback dimensions (`img.naturalWidth || r.width`, line 93), but loading only runs `loading.hidden = true`. Recalculate the current page’s pan and apply its zoom when the image loads.

- [P1] src/LocalDm.Tablet/wwwroot/photo-viewer.js:435 — Stale overlay state survives reopening. Closing cancels banner/toast timers without hiding either element; reopening reuses the DOM, so old messages remain indefinitely, violating the 6-second/1.5-second limits. Reset visibility on close or open. All occurrences found by grep: banner visibility at lines 474/478 and timer cancellation at 436; toast visibility at 137/139 and cancellation at 440.

- [P1] src/LocalDm.Tablet/wwwroot/photo-viewer.js:353 — Closing while the wake-lock request is pending can leave the screen locked afterward. `wakeLockSentinel = await navigator.wakeLock.request("screen")` can complete after `releaseWakeLock()` has already found no sentinel. Track the opening instance and immediately release any lock returned after that instance closes.

Bloat: none identified.

Pre-existing problems: none confirmed within the reviewed files.