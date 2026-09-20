# UAC Guard

UAC Guard uses gsudo's explicit credentials cache to let Codex run authorized
administrator commands without a UAC prompt for every command. It does not
elevate the ChatGPT GUI, disable UAC, or change `EnableLUA`.

The status overview provides a one-click install/repair action. After one UAC
approval, it installs or repairs the exact `gerardog.gsudo` package from the
winget source, verifies the expected gsudo executable exists, then deploys the
protected host, ACL, and scheduled task. Session start/stop controls remain
separate from package and component setup.

## Authorization modes

### Codex process lifecycle (recommended)

Guard Center detects the Codex app-server automatically and starts the
pre-authorized task without another UAC prompt. The cache is bound to that PID
and its children. When Codex exits, the protected helper revokes the cache;
when Codex starts again, Guard Center automatically binds the new PID.

### Guard Center lifecycle (high risk)

Guard Center automatically starts a pre-authorized Highest Privileges task at
launch. A protected helper creates a `-p 0` gsudo cache and monitors the current
Guard Center PID. Codex may restart without losing access. When Guard Center
fully exits, the helper revokes the cache; the application also performs a
best-effort revocation during normal shutdown.

This mode is deliberately broader: while it is active, another process running
as the same Windows user can also request elevation through gsudo. The UI shows
this boundary and requires explicit confirmation before enabling the mode.

## Agent behavior

The project-root `AGENTS.md` tells Codex to inspect `gsudo status --json` before
an administrator operation and to consume only a cache created by UAC Guard.
Codex must never create or broaden the cache itself. If no cache is available,
it directs the user back to UAC Guard instead of generating an unexpected UAC
prompt.

## Verification

The lifecycle prototype was started through the scheduled task, then consumed
by a medium-integrity PowerShell whose parent was not Codex. A genuine
administrator-only `fltmc filters` command completed through gsudo with exit
code zero and no new UAC prompt.
