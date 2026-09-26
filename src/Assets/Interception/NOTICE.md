# HuaJuan Interception compatibility payloads

`interception-sparse.dll` is a locally built, x64-compatible modification of the
Interception 1.0.1 user-mode library from:

- https://github.com/oblitum/Interception
- upstream commit `39eecbbc46a52e0402f783b872ef62b0254a896a`

The kernel drivers are not modified. The library keeps the original 20-device
ABI and exported function names, but context creation now tolerates missing
device handles. All public operations reject missing or invalid handles.

This allows HuaJuan to keep using the Interception mouse devices
(`interception10` through `interception19`) when the Interception keyboard class
filter is intentionally detached.

`interception-shim.dll` is an app-local x64 proxy used only by HuaJuan. It loads
the sparse library as `guardcenter-interception-real.dll`, resolves the intended
mouse by the stable hardware ID in `guardcenter-huajuan-shim.ini`, and remaps
HuaJuan's synthetic mouse output to that device. Non-zero Interception input
filters requested by HuaJuan are rejected so a transient detector context
cannot capture physical mouse movement without forwarding it.

The shim does not modify the kernel drivers, Windows device numbering, or
system-wide DLL search paths.

The modified source and deterministic probe used for this local build are kept
under:

`artifacts/interception-sparse-context-source`

Interception's repository documents LGPL terms for non-commercial library use
and separate commercial licenses. Review the upstream licensing terms before
redistributing Guard Center or this binary.
