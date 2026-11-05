# CiciPDA - development status

Publisher: CiciByte Technology, Inc. (cicibyte.com). Last audit: 2026-11-06. **V1.0 is not declared: nothing has been validated on physical hardware.**

## Architecture (current)
| Layer | Location | Responsibility |
| --- | --- | --- |
| Core library `CiciPDA.Core` | `src/core` | All logic: `Orchestrator` (state machine, stages, health, reconnect), `Adb`, `Companion` + `ReleaseTrust`/`ApkSigning`/`Pins` (install and trust), `Tunnel` (AES-GCM channel, SOCKS5 relay for TCP and UDP), `WinNet` + `NetshPlan` (tun2socks/Wintun host, routes, DNS, MTU, recovery journal, verification, rollback check), `Hotspot` (Mobile Hotspot sharing + client check page), `NativeLinks` (iPhone/Android hotspot interface selection), `AppUpdater`, `Diagnostics`, `Storage`, `Log` |
| WPF application | `src/app` | Shell, 8 pages, view model, orb/chart controls |
| Android companion | `src/agent` | `RelayService` (foreground), `PairingStore` (Keystore), `MainActivity`, shared `Relay` (protocol v3); also the legacy `app_process` launcher `TunnelMain` -> `agent.dex` |
| Tests | `tests/CiciPDA.Tests` | xUnit |
| Release tooling | `installer/`, `tools/ReleaseSigner`, `tools/make-icon.ps1` | Inno Setup script, build + validation scripts, signing |

Data path (Phone VPN / USB): Windows apps -> Wintun adapter `CiciPDA` -> tun2socks -> loopback SOCKS5 relay -> AES-256-GCM channel over `adb forward` -> companion service -> the phone's own network. See `PROTOCOL.md` for exactly what is carried (TCP yes; UDP datagrams yes; DNS = UDP/53 through the phone; ICMP no; IPv6 only as described).

Connected is reported only after: phone data path, adapter created, default route resolves to the tunnel, DNS, HTTP and HTTPS succeed. The decision uses *internet* checks (DNS, HTTP, HTTPS must not all fail); adapter readiness and the route are *local* checks and are labelled as such. UDP (a real DNS query over UDP), IPv6 behaviour and the Windows connectivity status (NCSI, observed through `Get-NetConnectionProfile`, never forced) are reported as informational. After teardown the app verifies rollback (adapter gone, pinned routes deleted, traffic no longer routed into the adapter) and logs the result.

## Audit 2026-11-06: findings and fixes
| # | Finding | Severity | Resolution |
| --- | --- | --- | --- |
| 1 | `PROTOCOL.md` said the key travelled "over stdin", but the companion receives it as an **Intent extra** (`am ... --es key`), so on the phone it is in the `am` arguments briefly. Also the key was **persistent** (DPAPI file on the PC, plain SharedPreferences on the phone), and a system restart could resume the relay after a disconnect. | High (doc mismatch + stale-secret risk) | Key is now **session-scoped**: fresh per connection, never on the PC's disk, sent through `adb shell` **stdin** (so not in the PC command line), Intent-delivered to a service callable only with `android.permission.DUMP`, stored **Keystore-wrapped** (allowBackup=false), deleted on STOP/UNPAIR/failed start and by an idle watchdog (180 s). "Unpair companion" added to Devices. Documentation rewritten to the exact flow, including the brief on-phone exposure. |
| 2 | Hotspot previous configuration was saved only after the start script finished, did not preserve the band, was not restored when start failed after reconfiguring, and crash/reboot recovery could not restore it because the upstream profile no longer existed. | Medium | Snapshot (SSID, passphrase, band) is read and DPAPI-saved **before** any change; band is preserved; failed start restores; recovery works without the adapter, using any capable profile, and acts **only if the hotspot still carries the SSID CiciPDA set** (never touches a user's own hotspot). Limits below. |
| 3 | An attempt where DNS, HTTP and HTTPS all failed could still end *Limited* because local checks (route) passed. | Medium | Failing all three internet checks now fails the connection. |
| 4 | No real UDP check; IPv6 protection reported as success without confirming the routes. | Medium | UDP DNS check added; IPv6 reported as two facts (protection really installed? IPv6 internet reachable?); `Ipv6Protected` is true only if the address and both routes were installed. |
| 5 | Rollback was assumed, not verified. | Medium | `RollbackCheck` verifies adapter removal, pinned route removal (with one retry) and the best route; result logged. |
| 6 | Revoked USB-debugging authorization showed a generic "disconnected" message. | Low | Distinct message. |
| 7 | Documentation cited stale test counts (41/75), "13" diagnostic tests and old file names. | Low | Corrected: **85 tests**, **14 diagnostics**, per-platform asset names. |
| 8 | Linux/macOS assets requested: no such builds exist (WPF, Wintun, netsh). | Info | Release assets are named per platform; Linux and macOS are documented as **not available**. |

### Hotspot restore: what can and cannot be restored
The Windows Mobile Hotspot API lets CiciPDA read and write the **SSID, passphrase and band**. Those are snapshotted and restored. Other Mobile Hotspot options (authentication type, timeout/power saving, "turn on remotely") are **never modified** by CiciPDA, so they are not changed and need no restore. If the previous configuration cannot be read the hotspot is not started. If the snapshot file is lost, the SSID/passphrase CiciPDA set stay configured in Windows and the log says so. A hotspot that already runs is never taken over, and the upstream is never switched silently: if the CiciPDA adapter has no Windows profile or Windows reports sharing disabled, the start is refused with the reason and the manual Internet Connection Sharing alternative is named.

## Build and test results (2026-11-06)
* `dotnet build` (core, app, tests, ReleaseSigner): 0 errors, 0 warnings.
* `dotnet test`: **85 passed, 0 failed** (non-elevated Windows PC, no phone). Coverage: relay protocol against the real Java relay (TCP, UDP IPv4/IPv6, 14 KB datagram, status, wrong key, SOCKS CONNECT/UDP ASSOCIATE, restart with the same key); release trust chain (tampered APK/metadata, same-server hash not trusted, unsafe names); APK certificate parser vs `apksigner`; install-failure classification; session-key handling (no file, masked in logs, stdin source assertion); Android manifest/service source assertions; `NetshPlan` (split routes, non-persistent, DNS scope, MTU clamp, bypass ordering, IPv6); recovery of a real orphan `tun2socks.exe` (and that an unrelated process is never killed); iPhone/Wi-Fi interface selection; hotspot capability queries (real WinRT) and the client probe page; UDP DNS check, IPv6 report, rollback route parser; updater manifest verification; ADB location (bundled-first, override, fallback); real release artifacts against the compiled-in production pins.
* Release build (`installer/build-installer.ps1`): clean self-contained win-x64 publish, Inno Setup 6.7.3 compile, then `validate-release.ps1` **PASSED**: publish folder contains every runtime file, ADB files byte-identical to `third_party/adb`, companion APK hash equals the signed metadata, update manifest hash equals the installer, and the **installer was actually executed** (per-user, silent, into a temp folder), every file was checked in the installed copy and the uninstaller removed it. An MSBuild target also fails the build if a required runtime file is missing from the output.
* Live update channel (published v2.1.0, verified earlier): the updater was offered 2.1.0 as a 2.0.0 client, nothing as a current client, and the real installer downloaded and passed the signed checksum. **The 2.1.1 contents described here are not yet published.**

## Installer contents (verified in the installed copy)
`CiciPDA.exe` + self-contained .NET runtime, `adb\adb.exe`, `adb\AdbWinApi.dll`, `adb\AdbWinUsbApi.dll`, `tools\wintun.dll`, `tools\tun2socks.exe`, `agent.dex`, `companion\cicipda-companion.apk`, `companion\companion.json`, `companion\companion.json.sig`, `licenses\THIRD_PARTY.md`, `licenses\LICENSE`. Default folder `C:\Program Files\CiciByte\CiciPDA` (the script setting is checked automatically; installing into Program Files itself needs UAC and has **not** been run).

## Tested on physical hardware
**Nothing.** Everything below is PC-tested only or not tested:
* Real Android phone: APK install/update, foreground service, Keystore wrapping, watchdog, notification flow, OEM behaviour, USB unplug/restart/authorization revocation, status JSON, signal parsing.
* Elevated Windows: adapter, routes, DNS, MTU, IPv6 routes, rollback on a real connection, NCSI reaching Internet, transport bypass with ADB-over-Wi-Fi, browsers/desktop apps/QUIC through a phone.
* Mobile Hotspot sharing the Wintun profile, a second device, the client check page (Windows Firewall may block it), restore of the previous hotspot configuration, crash/reboot recovery.
* iPhone USB (Apple Ethernet adapter) and Personal Hotspot over Wi-Fi.
* UAC install into `C:\Program Files\CiciByte\CiciPDA`, upgrade-over-existing, the in-app Update button end to end, SmartScreen.

The step-by-step plan is `HARDWARE_TEST_PLAN.md`.

## Known limitations
* Not a full layer-3 VPN and not WireGuard. TCP and UDP datagrams are relayed; ICMP is not; IP fragments are dropped; Windows DNS is UDP/53 relayed through the phone.
* IPv6: protected only when the setting is on and routes were installed; end-to-end IPv6 depends on the phone's network.
* iPhone and Android-hotspot-join modes use Windows' own connection (no tunnel, no CiciPDA encryption); Wi-Fi sharing is offered only for the tunnel methods.
* Android may stop the foreground service on aggressive battery managers; the PC reconnects with a new key.
* No Authenticode signature yet; Linux and macOS builds do not exist.

## Defects found after 2.1.1 shipped (fixed in 2.1.2)
* **First run after install failed with "CreateProcess failed; code 740".** The installer flags `CiciPDA.exe` to run as administrator, but the post-install launch used plain `CreateProcess`, which refuses elevated-flagged executables. The earlier validation ran the installer with `/VERYSILENT`, which never launches the app, so it could not catch this. Fix: the launch entry now uses `shellexec` (UAC prompt, then the app starts) and also relaunches the app after a silent in-app update. Reproduced the 740 behaviour locally; `validate-release.ps1` now asserts the `shellexec` flag. **The interactive UAC-then-start sequence itself has not been executed by automation (it needs a UAC click).**
* **No desktop shortcut:** the shortcut task was optional and unchecked. It is now checked by default; validation installs, checks the shortcut and its target, and checks the uninstaller removes it.
