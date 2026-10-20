# The runtime matrix

Generated: 2026-10-20T03:33:22Z

The raw artifacts of the run are in `artifacts/matrix/`. Every case is a
fresh Steam launch with the logs cleared beforehand, so a stale log can
never be read as a result. The analysis is in
[LINUX_ROOT_CAUSE.md](LINUX_ROOT_CAUSE.md).

| Case | Result | The game | BepInEx | The API |
|---|---|---|---|---|
| 1. true vanilla | PASS (no crash) | the original EOS SDK | not loaded | no answer |
| 2. proxy present, doorstop disabled | PASS (no crash) | the original EOS SDK | not loaded | answered |
| 3. proxy and BepInEx, no plugin | PASS (no crash) | the original EOS SDK | loaded | answered |
| 4. proxy, BepInEx and PEAKapi | PASS (no crash) | the original EOS SDK | loaded | answered |
| 5. proxy, BepInEx and PEAKapi, EOS disabled | PASS (no crash) | the EOS SDK is disabled | loaded | answered |

## What each case proves

| Case | What it proves |
|---|---|
| 1. true vanilla | The game runs with the original EOS SDK when the proxy is not in the process. |
| 2. proxy present, doorstop disabled | The proxy is still loaded by Wine, so this is **not** a vanilla baseline. |
| 3. proxy and BepInEx, no plugin | The loader works without PEAKapi. |
| 4. proxy, BepInEx and PEAKapi | The shipped configuration. |
| 5. like 4 with the EOS SDK disabled | A degraded mode, only for a local diagnosis. |
