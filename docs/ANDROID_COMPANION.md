# Android companion: lifecycle, permissions and OEM notes

## Lifecycle (what the PC does on Connect)
1. `adb devices -l` -> device must be `device` (authorized). `unauthorized` -> the user must tap **Allow** on the phone; CiciPDA never tries to bypass it.
2. Release verification on the PC (signed metadata, hash, pinned signer) -> `adb install -r` if the companion is missing or older (Android re-verifies the signature).
3. First install only: the companion's screen is opened once so the user can read what it does and tap **Allow notifications** (Android 13+). CiciPDA never grants runtime permissions through adb.
4. `am start-foreground-service` (key on stdin) -> the service shows its notification and starts the relay on `127.0.0.1`.
5. `adb forward tcp:0 tcp:<port>` + authenticated status request (`S`) must succeed before anything else happens.
6. While connected: status poll every 5 s. If the companion stops answering twice, or the device disappears from `adb devices` (USB unplug, phone restart), the PC tears down routes/DNS/adapter and, if enabled, reconnects with the configured attempts/delay.
7. Disconnect: companion service is stopped (`ACTION_STOP`), the adb forward is removed, Windows state is restored.

## Permissions and consent (nothing is bypassed)
| Item | Who grants it |
| --- | --- |
| USB debugging authorization | The user, on the phone (RSA key prompt) |
| "Install via USB" on some OEMs | The user, in Developer options |
| Notification permission (Android 13+) | The user, on the companion screen. The service still runs without it but its notice may be hidden |
| VPN consent | **Not used.** The companion does not use `VpnService`; it is a relay, not a VPN |
| Foreground service type | Declared `specialUse` in the manifest (required from Android 14) |

## OEM-specific restrictions
* **Xiaomi / Redmi / POCO (MIUI, HyperOS):** enable *USB debugging (Security settings)* and *Install via USB* in Developer options (both can require a signed-in Mi account/SIM); installs otherwise fail with `INSTALL_FAILED_USER_RESTRICTED`. Set the companion's *Autostart* on and Battery saver to *No restrictions*.
* **Oppo / Realme / OnePlus (ColorOS/OxygenOS):** *Disable permission monitoring* (Developer options) is needed for adb installs on some builds; set battery usage to *Allow background activity*.
* **Vivo (Funtouch/OriginOS):** *USB debugging (security settings)* and installation confirmation prompts; allow background high power consumption.
* **Samsung (One UI):** *Auto Blocker* blocks installs over USB and USB debugging prompts; turn it off for the session. Exclude the app from *Sleeping apps / Deep sleeping apps*.
* **Huawei / Honor:** adb install may require *Allow ADB debugging in charge-only mode*; manage launch manually (allow background activity).
* **Pixel / near-stock:** no extra steps beyond USB debugging.
* Android may stop or kill any background component under memory pressure or aggressive battery management; the PC detects this through the status poll and reconnects.

## Failure messages shown to the user
Mapped in `CompanionManager.Explain`: blocked install, signer mismatch (installed copy signed with a different key -> uninstall it on the phone), downgrade, low storage, unsupported ABI/API.

## What has NOT been tested on a real device
Everything on this page. The relay protocol, install-failure parsing and release trust are covered by automated tests on the PC; the Android behaviour (service start, notification flow, OEM prompts, restart/unplug handling) needs physical devices.
