# Android companion: lifecycle, permissions and compatibility

Package `app.cicibyte.cicipda`, currently **2.1.3 (versionCode 20103)**, written in Java, built without Gradle by `src/agent/build.ps1` (javac + d8 + aapt2 + apksigner). minSdk 24, targetSdk 34.

## Lifecycle (what the PC does on Connect)
1. `adb devices -l`: the device must be `device`. `unauthorized` -> the user taps **Allow** on the phone; CiciPDA never tries to bypass it.
2. Release verification on the PC (signed metadata -> APK hash -> pinned signer certificate), then `adb install -r` if the companion is missing or older. Android re-verifies the APK signature and refuses an update signed by a different key.
3. First install only: the companion screen is opened once so the user can read what it does and tap **Allow notifications** (Android 13+). CiciPDA never grants runtime permissions through adb.
4. A fresh **session key** is created and the foreground service is started with `am start-foreground-service` (see PROTOCOL.md for exactly where the key travels).
5. `adb forward tcp:0 tcp:<port>` + an authenticated status request (`S`) must succeed before anything else happens.
6. While connected the PC polls status every 5 s. If the companion stops answering twice, or the device leaves `adb devices` as authorized (USB unplug, phone restart, **revoked USB-debugging authorization** - reported as such), the PC removes routes/DNS/adapter and, if enabled, reconnects with a new key.
7. Disconnect: `ACTION_STOP` (service stops and **deletes the session key and its Keystore wrapper**), adb forward removed, Windows state restored and checked.
8. If the PC vanishes without STOP, the service ends the session itself after 180 s without an authenticated connection.

## Permissions and consent (nothing is bypassed)
| Item | Who grants it |
| --- | --- |
| USB debugging authorization | The user, on the phone (RSA key prompt). Revoking it ends the session. |
| "Install via USB" (some OEMs) | The user, in Developer options |
| Notification permission (Android 13+) | The user, on the companion screen. The service still runs without it, but its notice may be hidden |
| VPN consent | **Not used.** The companion does not use `VpnService`; it is a relay |
| Foreground service type | `specialUse` (+ `FOREGROUND_SERVICE_SPECIAL_USE`), required from Android 14 |
| Caller permission on the service | `android.permission.DUMP` (only the adb shell/system) |

## Android version assumptions (by design, not yet device-tested)
| Android | Assumption in the code |
| --- | --- |
| 7.0-7.1 (API 24-25) | `startForeground(id, notification)`; no notification channels; Keystore AES-GCM is available (API 23+) |
| 8.0-11 (API 26-30) | Notification channel created; started with `start-foreground-service` and `startForeground()` called immediately |
| 12-12L (API 31-32) | Background foreground-service start restrictions: if Android refuses (`ForegroundServiceStartNotAllowedException`) the service stops cleanly and the PC reconnects |
| 13 (API 33) | `POST_NOTIFICATIONS` is a runtime permission; absence does not stop the service |
| 14+ (API 34+) | Foreground service type `specialUse` declared and passed to `startForeground` |
| 15+ | `specialUse` has no 6-hour timeout (unlike `dataSync`); OEM battery managers remain the main risk |

## OEM-specific restrictions
* **Xiaomi / Redmi / POCO (MIUI, HyperOS):** enable *USB debugging (Security settings)* and *Install via USB* in Developer options (may require a signed-in account/SIM); otherwise `INSTALL_FAILED_USER_RESTRICTED`. Set the companion's *Autostart* on and Battery saver to *No restrictions*.
* **Oppo / Realme / OnePlus:** on some builds *Disable permission monitoring* is required for adb installs; allow background activity.
* **Vivo:** *USB debugging (security settings)* and the install prompt; allow background high power consumption.
* **Samsung (One UI):** *Auto Blocker* blocks installs over USB; turn it off for the session; exclude the app from *Sleeping apps*.
* **Huawei / Honor:** may need *Allow ADB debugging in charge-only mode*; allow background activity manually.
* **Pixel / near-stock:** USB debugging only.

## Failure messages shown to the user
`CompanionManager.Explain`: blocked install, signer mismatch (installed copy signed with a different key -> uninstall it), downgrade, low storage, unsupported ABI/API. Revocation and disconnection have distinct messages.

## Test status
Automated on the PC: release trust chain, install-failure classification, relay protocol (against the real Java relay), manifest/service source assertions (service protected by `DUMP`, no backup, single service, key deletion present, Keystore used). **Not run on any Android device**: installation, service start, notification flow, Keystore wrapping, watchdog, restart/unplug, OEM behaviour. See HARDWARE_TEST_PLAN.md.
