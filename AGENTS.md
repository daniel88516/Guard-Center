# Guard Center agent instructions

## UAC Guard administrator commands

- Before running a shell command or external executable, first run `C:\Program Files\gsudo\Current\gsudo.exe status --json` at standard user integrity. This status probe is the only routine command that does not itself go through gsudo.
- When that status reports `CacheAvailable: true`, run every shell command and external executable through gsudo, including ordinary development, inspection, build, and test commands. Do not decide command-by-command whether elevation is necessary.
- When that status reports `CacheAvailable: false`, continue normally at standard user integrity. Do not stop the task merely because the cache is unavailable, and do not ask the user to establish an administrator session unless the requested operation actually cannot proceed without one.
- For a native executable, use `C:\Program Files\gsudo\Current\gsudo.exe --direct <absolute-executable> <arguments>`.
- For a PowerShell command, invoke a new elevated PowerShell through the same absolute gsudo path and preserve exact argument boundaries whenever the cache is available.
- Never run `gsudo cache on`, `-p 0`, or change the cache scope yourself. Only consume a cache that UAC Guard created after the user selected and confirmed its authorization mode.
- In Codex-process mode, the cache is limited to the Codex app-server PID and its children. In Guard-Center-lifecycle mode, the cache is intentionally broader and remains available to the same Windows user until Guard Center exits or the user terminates the session.
- If an elevated command fails, report its exact exit code and output. Do not silently retry by launching another UAC flow.
- When the user asks to end administrator access, run `C:\Program Files\gsudo\Current\gsudo.exe -k` and verify that `CacheAvailable` is false.
