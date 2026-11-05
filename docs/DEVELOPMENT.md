# CiciPDA — development status

Last updated: 2026-11-05. This document states what was built, what was actually tested, and what still needs hardware.

## Architecture

| Layer | Location | Responsibility |
| --- | --- | --- |
| Core library (`CiciPDA.Core`) | `src/core` | All non-UI logic |
| WPF application | `src/app` | Shell, pages, view model, controls |
| Android helper | `src/agent/TunnelMain.java` → `src/app/agent.dex` | Phone-side relay (no root) |
| Tests | `tests/CiciPDA.Tests` | xUnit, includes loopback tests against the real Java helper |

Core components: `Log` (structured log + activity feed, secret masking), `Storage` (settings, history, devices, DPAPI secrets), `ProcessRunner` (argument-list process launching, no shell strings), `Adb` (discovery, helper deployment, forwards), `Tunnel` (AES-GCM channel + SOCKS5 relay), `WinNet` (Wintun/tun2socks host, routes, DNS, recovery journal, verification, hotspot), `Orchestrator` (state machine, monitor, health/reconnect, cleanup), `Diagnostics`.

### Data path (Phone VPN / USB)

Windows apps → Wintun adapter `CiciPDA` → tun2socks → loopback SOCKS5 relay (`SocksRelay`) → `AgentChannel` (AES-256-GCM, per-connection and per-direction keys derived with HMAC-SHA256) → `adb forward` → helper on the phone → phone's own network.

* TCP CONNECT is relayed. UDP/53 (DNS) is converted to DNS-over-TCP through the phone. Other UDP is dropped and counted.
* Routes: `0.0.0.0/1` + `128.0.0.0/1` (and `::/1`, `8000::/1` when IPv6 protection is on) through the adapter, `store=active` so nothing survives a reboot. For ADB-over-Wi-Fi the phone's IP is pinned to its original interface to prevent a routing loop.
* **Connected** is reported only after: phone data path probe, adapter creation, default route resolves to the tunnel, DNS resolves, HTTP `connecttest.txt` matches, HTTPS succeeds. Partial success yields *Limited Connectivity*; no success tears everything down and reports *Connection Error*. Windows NCSI is read, never forced.
* Cleanup is journaled (`data/journal.json`): orphan tun2socks, bypass routes, adb forwards and hotspot are undone on the next start after a crash.

### Not a WireGuard VPN

The requested "WireGuard-compatible" architecture was **not** implemented: WireGuard needs an Android `VpnService` app, which this repository does not contain. The existing ADB-relay design was kept and its crypto was repaired (see below). "Phone VPN" in the UI means "Wintun adapter routed through the phone", and the UI/README say so.

## Defects found in the audit and what was done

| Finding | Action |
| --- | --- |
| Sources did not build (XAML `MC3072`); csproj referenced missing `bin\wintun.dll`, `tun2socks.exe`, `app.ico`; `fetch-tools.ps1` wrote to the wrong folder | Rewrote project; tools live in `src/app/tools`; script fixed and hash-checks `wintun.dll` |
| Hand-written Wintun P/Invoke wrong (session handle, LUID, MTU API, route row layout) | Removed; tun2socks owns the Wintun adapter; configuration via `netsh` with argument lists |
| AES-GCM stream wrapper reused nonces across connections (same key, counters restarting at 0) | Protocol v2: salt per connection, separate keys per direction, counters never reused |
| Helper start waited for the long-running agent to exit (hang); key passed on a command line | Agent prints `READY`, key sent over stdin, agent exits when stdin closes; random remote port; `adb forward tcp:0` avoids port conflicts |
| Registry write to force NCSI probing; hardcoded hotspot password; hardcoded `C:\Users\Captain` adb path; invalid `runas` + `UseShellExecute=false`; broken ICS COM calls | Removed; real verification; passphrase generated/DPAPI-stored; adb located by search; hotspot rewritten (Mobile Hotspot API) |
| README/config advertised "stealth" carrier-hiding | Removed (out of scope by requirement) |
| `obj/`, `bin/`, `out/` tracked in git | Untracked; `.gitignore` added |

## Build and test

```powershell
cd src\agent;  .\build.ps1                 # only if TunnelMain.java changed (needs Android SDK build-tools + JBR)
cd ..\app;     .\fetch-tools.ps1           # wintun.dll + tun2socks.exe -> tools\
cd ..\..;      dotnet build src\app\CiciPDA.csproj -c Release
dotnet test tests\CiciPDA.Tests
```

Development preview (renders a page to PNG; `--demo` fills **sample numbers for screenshots only**, never used in normal runs):
`CiciPDA.exe --preview Connect out.png --demo`

### Build status
* `src/core`, `src/app`, `tests` build with 0 errors, 0 warnings (.NET SDK 10 / target `net9.0-windows`).

### Tests performed (2026-11-05, this machine, non-elevated, no phone attached)
* 14 xUnit tests pass: ADB output parsing, signal parsing, DPAPI round trip, log secret masking, key derivation, **C# client ↔ real Java helper over loopback** (100 KB payload across many frames, wrong key rejected, unreachable target reported, SOCKS5 CONNECT relayed and failure reply), orchestrator refuses to connect without admin and never reports *Connected*, journal round trip.
* App starts, runs and renders all 8 pages (screenshots rendered from the WPF visual tree at 1280×720); no errors in the log.

## Still requires hardware / elevation (NOT verified)

* An elevated run creating the Wintun adapter, routes and DNS, and verifying a real connection (needs Administrator).
* Android phone over USB: helper deployment through `app_process` on different Android versions and OEMs, ADB authorization flow, signal parsing from `dumpsys telephony.registry`.
* ADB-over-Wi-Fi (bypass-route logic).
* Android Wi-Fi hotspot and iPhone (Wi-Fi or Apple USB adapter) detection paths.
* Wi-Fi sharing from the PC through Windows Mobile Hotspot (PowerShell/WinRT; behaviour with a tunnel-only profile is unknown).
* Reconnect/recovery under real cable pull; crash-recovery journal against a real orphan tun2socks.
* Windows NCSI reaching *Internet* on the adapter profile.

## Known limitations / remaining work

* Only TCP and DNS cross the tunnel; QUIC/other UDP are dropped. ICMP does not traverse it (latency/“probe loss” are TCP connect probes through the phone).
* No tray icon, no installer, no `app.ico` (the csproj uses one if present).
* Language support covers navigation, status and the main page (English/Turkish); other pages are English.
* Single-file publish is configured (`-p:PublishSingleFile=true`) but not yet exercised.
* The repo still contains the old `PdaNetPortable.exe` release artifact and old `data/` files from the previous version.
