# CiciPDA — development status

Last updated: 2026-11-06 (companion deployment added; see the last section). This document states what was built, what was actually tested, and what still needs hardware.

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

---

## Companion deployment (added 2026-11-06)

* **Android companion** (`src/agent/android`, package `app.cicibyte.cicipda`): Java, no Gradle. A foreground `RelayService` (type `specialUse`, started and stopped only by the adb shell through `android.permission.DUMP`), a small `MainActivity` that explains the app and asks for the notification permission, and the shared `Relay` (protocol v3, see PROTOCOL.md). Java was kept rather than migrating to Kotlin: the relay is about 250 lines of plain `java.net`/`javax.crypto`, and Kotlin would add a runtime dependency and build tooling without benefit.
* **Deployment manager** (`src/core/Companion.cs`): inspects model, Android version, installed companion version and notification permission; verifies the release; installs or updates with `adb install -r`; classifies and explains failures (blocked "install via USB", signature mismatch, downgrade, storage, ABI); starts the service (key via stdin); forwards a port and waits for an authenticated status reply.
* **Release trust** (`ReleaseTrust.cs`, `ApkSigning.cs`, `Pins.cs`, `tools/ReleaseSigner`): see RELEASE.md. A newer companion is downloaded only from the pinned GitHub release channel, after the signed metadata verifies.
* **Orchestrator** reports nine stages (detect, authorize, companion, install, service, tunnel, adapter, network, internet) and six independent status layers (device connected, companion installed, companion running, tunnel connected, Windows network configured, internet verified), shown separately in the UI. Health polling uses the authenticated status command and triggers reconnect if the companion stops answering.
* **UDP** is relayed (protocol v3), so QUIC and other UDP no longer fall back; see PROTOCOL.md for the remaining limits (no ICMP).
* **Installer**: `installer/CiciPDA.iss` and `installer/build-installer.ps1` (Inno Setup; self-contained publish including adb, Wintun, tun2socks, companion and licences). `THIRD_PARTY.md` lists licence obligations (tun2socks is GPL-3.0; adb redistribution terms need review).

### Tested (2026-11-06, no phone, non-elevated)
* 31 xUnit tests pass, including: loopback protocol v3 against the real Java relay (TCP, UDP datagrams, SOCKS UDP ASSOCIATE, status command, wrong key); the trust chain (bundled release verifies; tampered APK rejected by hash; tampered metadata rejected by signature; matching hash without a valid metadata signature rejected; unsafe file name rejected; APK without signing block rejected); the C# APK-certificate parser agrees with `apksigner`'s certificate digest; install-failure classification; DPAPI-protected pairing key.
* The companion APK builds, is aligned, and is signed (`apksigner verify` passes for v2/v3).
* `dotnet publish` (self-contained win-x64) produces a runnable 148 MB folder containing tools, companion, adb and agent; the published exe starts cleanly.

### NOT tested (needs a phone, administrator rights, or other tooling)
* Installing and starting the APK on any real Android device (all Android versions and OEM variants), the first-run notification flow, foreground-service survival, the status JSON network fields, phone restart, USB unplug during a session, the APK update path.
* Elevated adapter/route/DNS/rollback, DNS failure handling, QUIC/UDP through a real phone, IPv6 behaviour, Windows NCSI reaching Internet, browser and background-service behaviour, Windows restart.
* The Inno Setup installer (Inno Setup is not installed here), code signing, a fresh-Windows install, and the update download against a real GitHub release.
* Production signing and metadata keys have not been created; the development keys are in use.

---

## Installer and auto-update (added 2026-11-06)

* The portable build is gone: `PdaNetPortable.exe` and the old `data/` files were removed; bundled `adb` moved to `third_party/adb`. User data now lives in `%LocalAppData%\CiciByte\CiciPDA`.
* `installer/CiciPDA.iss` installs to `C:\Program Files\CiciByte`, adds Start-menu/optional desktop shortcuts, flags `CiciPDA.exe` to run as administrator, and bundles the licence files. `installer/build-installer.ps1` builds it and writes the signed update manifest.
* `AppUpdater` (core) + UI: automatic check 20 s after start and every 6 h (toggle in Settings), header *Update* button, Help page *Check for updates*. Trust: signed manifest, pinned GitHub HTTPS host, signed SHA-256, user confirmation, network teardown before the installer runs.
* Production keys and pins created; `IsDevelopmentTrust` is false.

### Tested
* 41 tests pass, including updater manifest verification (wrong key, tampering, unsafe names, bad checksum, non-GitHub channel) and verification of the actually built release artifacts (`update.json`, installer SHA-256, companion metadata) against the compiled-in production pins.
* `CiciPDA-Setup-2.1.0.exe` (47.7 MB) compiles with Inno Setup 6.7.3.

### NOT tested
* Running the installer (needs UAC elevation), install location, shortcuts, upgrade-over-existing, uninstall, and the silent update hand-off; the download path against a real GitHub release (none published yet).
