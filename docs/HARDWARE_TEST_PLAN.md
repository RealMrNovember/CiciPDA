# V1.0 hardware test plan

V1.0 may be declared only when every **Required** case below has been executed on real hardware and recorded. Status legend:

* **Automated** - covered by `dotnet test` (PC-only; proves logic, not hardware behaviour)
* **Manual** - a person performs and observes it
* **Physical device** - needs a real phone / second device / Wi-Fi hardware / UAC
* **NOT VERIFIED** - no physical execution yet (the state of every case today)

Record for each run: date, Windows build, phone model + Android version, CiciPDA version, result, notes. Pre-condition for all: build under test is the released installer, not a dev build. Keep `%LocalAppData%\CiciByte\CiciPDA\cicipda.log`.

| # | Case | Type | Required | Status |
| --- | --- | --- | :---: | --- |
| 1 | Clean Windows 10 install | Manual, Physical device | yes | NOT VERIFIED |
| 2 | Clean Windows 11 install | Manual, Physical device | yes | NOT VERIFIED |
| 3 | Android USB pairing | Manual, Physical device | yes | NOT VERIFIED |
| 4 | Automatic APK installation / update | Manual, Physical device (+ Automated trust chain) | yes | NOT VERIFIED |
| 5 | Android service startup | Manual, Physical device | yes | NOT VERIFIED |
| 6 | TCP internet connectivity | Manual, Physical device (+ Automated relay) | yes | NOT VERIFIED |
| 7 | UDP and QUIC | Manual, Physical device (+ Automated relay) | yes | NOT VERIFIED |
| 8 | DNS | Manual, Physical device (+ Automated query/UDP check) | yes | NOT VERIFIED |
| 9 | IPv4 | Manual, Physical device (+ Automated plan) | yes | NOT VERIFIED |
| 10 | IPv6 | Manual, Physical device | yes | NOT VERIFIED |
| 11 | Windows NCSI | Manual, Physical device | yes | NOT VERIFIED |
| 12 | Browser and desktop application connectivity | Manual, Physical device | yes | NOT VERIFIED |
| 13 | Wi-Fi hotspot startup | Manual, Physical device | yes | NOT VERIFIED |
| 14 | Second-device connection | Manual, Physical device | yes | NOT VERIFIED |
| 15 | Second-device DNS and HTTPS verification | Manual, Physical device | yes | NOT VERIFIED |
| 16 | Hotspot disconnect and recovery | Manual, Physical device | yes | NOT VERIFIED |
| 17 | iPhone USB connection | Manual, Physical device | yes | NOT VERIFIED |
| 18 | iPhone Personal Hotspot over Wi-Fi | Manual, Physical device | yes | NOT VERIFIED |
| 19 | Clean disconnect and network restoration | Manual, Physical device (+ Automated plan/recovery) | yes | NOT VERIFIED |

## Cases

### 1-2. Clean Windows 10 / Windows 11 installation
1. Fresh VM or PC with only Windows updates; no Android SDK, no .NET, no adb on PATH.
2. Run `CiciPDA-<version>-windows-x64-Setup.exe`, accept UAC. Expect: installs to `C:\Program Files\CiciByte\CiciPDA`, Start-menu entry, launches (as administrator).
3. Check the installed folder contains every file listed in `DEVELOPMENT.md` (installer contents).
4. Open Help: version matches; Settings: nothing references a developer path. Run *Network Diagnostics > Run all tests*: "Bundled tools" passes.
5. Update path: install an older build, publish a newer release, press **Update**; expect confirmation, disconnect/restore if connected, silent upgrade, restart into the new version, folder unchanged.
6. Uninstall via *Apps & features*; expect the application folder removed and no adapter/routes left.

### 3. Android USB pairing
1. Phone with USB debugging OFF: expect the checklist to say what to enable. Turn it on, connect a data cable.
2. Expect the *Allow USB debugging?* prompt; before tapping Allow, Connect must report the phone as not authorized with the instruction (no bypass attempted). Tap Allow.
3. Revoke authorizations in Developer options while connected: expect a session end with "USB debugging authorization was revoked on the phone."

### 4. Automatic APK installation and update
1. Phone without the companion: press Connect. Expect stages Detect -> Authorize -> Check companion -> Install; install succeeds (confirm on the phone only if the OEM asks); the companion screen opens once.
2. Phone with an older companion (install a previous release APK): expect an in-place update, no data prompt, version shown updated in Diagnostics.
3. Install a differently signed `app.cicibyte.cicipda`: expect the signer-mismatch message with uninstall instruction.
4. OEM matrix (record each): Xiaomi/HyperOS ("Install via USB"), Samsung (Auto Blocker), Oppo/Realme, Vivo, Pixel. Expect the explained failure where blocked.
5. Tamper test (PC): replace the bundled APK byte; expect the trust error and no install.

### 5. Android service startup
1. After Connect, the notification "CiciPDA is sharing this phone's connection" is visible (Android 13+: after allowing notifications; without permission the service still runs).
2. Test Android 7, 8/9, 11, 12/12L, 13, 14, 15: the service starts; on 12+ a refused background start ends cleanly and the PC reconnects.
3. Force-stop the companion mid-session: PC detects within ~10 s and reconnects with a **new** session key.
4. Pull the USB cable and leave the phone: within ~3 minutes the service ends itself and the notification disappears; the key is gone (reconnect shows a fresh pairing).
5. *Devices > Unpair companion*: service stops, notification gone; next Connect works.
6. Check on the phone (adb): `adb shell run-as` is not available on release builds, so confirm indirectly: no `pairing` plaintext in `adb backup` output; `dumpsys activity services app.cicibyte.cicipda` shows the service only while connected.

### 6. TCP connectivity
Connect (Phone VPN, USB). Expect all six *Status layers* green, **Connected**. In Diagnostics: route, DNS, HTTP, HTTPS pass. Download a large file; compare speed counter with Task Manager. Verify the public IP seen by a web page equals the phone's carrier/VPN IP, not the PC's ISP.

### 7. UDP and QUIC
1. Diagnostics shows *UDP connectivity (DNS over UDP)* passing.
2. Open `https://www.google.com` in Chrome with QUIC enabled (`chrome://net-export` or DevTools protocol column shows h3). Expect h3 traffic and the *UDP datagrams* counters moving; disable QUIC and confirm h2 still works.
3. A UDP application (voice call, game): works; large datagrams (>14.9 KB) are dropped by design.

### 8. DNS
1. `nslookup example.com` and `Resolve-DnsName example.com`: answered by the DNS server configured in Settings, through the phone.
2. Change DNS in Settings: expect the confirmation dialog; next connection uses it.
3. Break DNS (set an unreachable server): expect the DNS check to fail, internet checks to fail and the attempt to report a clear error.
4. After disconnect: `Get-DnsClientServerAddress` shows no CiciPDA adapter and the original DNS.

### 9. IPv4
`route print -4` while connected: two `/1` routes via the CiciPDA adapter, the original default route untouched, a /32 pinned route only for ADB-over-Wi-Fi. After disconnect: none remain (also check after killing CiciPDA from Task Manager and restarting it: recovery cleans up).

### 10. IPv6
1. On a phone with IPv6: connect with leak protection ON; `route print -6` shows `::/1` and `8000::/1`; the Diagnostics IPv6 line reports protection ACTIVE and IPv6 reachable; `https://test-ipv6.com` result.
2. Phone without IPv6: protection ACTIVE, IPv6 not reachable; browsing falls back to IPv4.
3. Protection OFF: the line says traffic may bypass; confirm with a PC that has IPv6.

### 11. Windows NCSI
After Connected, within ~30 s the network icon / `Get-NetConnectionProfile` shows **IPv4Connectivity = Internet** for the CiciPDA profile and Diagnostics "Windows connectivity status" passes. Confirm CiciPDA never changes NCSI registry keys or probes (no registry writes).

### 12. Browser and desktop applications
Chrome/Edge/Firefox (HTTPS, QUIC), Windows Update check, Microsoft Store, OneDrive/Teams, a Steam or game download, `curl`, `git clone` over HTTPS and SSH. Background services reach the internet only through the tunnel (check with Resource Monitor).

### 13-16. Wi-Fi hotspot
1. Settings: enable Wi-Fi sharing, set SSID and passphrase. Pre-condition: Windows Mobile Hotspot **off**.
2. Connect; after internet verification, expect state *Hotspot active*. If Windows reports the CiciPDA profile unsupported, expect the explained reason and the Internet Connection Sharing alternative; **no unrelated connection (Ethernet/Wi-Fi) is ever shared**.
3. Second device joins: state *Client connected*. On it open the URL shown in the Connect page (hotspot gateway, token page): expect "DNS: OK HTTPS: OK" and state *Client internet verified*; also browse normally.
4. With Windows Firewall blocking the check page: state stays *Client connected* with a note (acceptable; verify manually).
5. Disconnect normally: hotspot stops, **previous SSID, passphrase and band are restored** (compare with the values noted before the test).
6. Kill CiciPDA during sharing, then start it: recovery stops the hotspot and restores the previous configuration. Reboot Windows during sharing: same on next start.
7. With Mobile Hotspot already on before connecting: Connect refuses sharing with the "turn it off first" message and does not alter it.
8. Failed start (disable the Wi-Fi adapter): previous configuration unchanged, clear failure message.
9. Change the SSID manually while CiciPDA shares, then disconnect: CiciPDA must **not** touch the user's new configuration.

### 17. iPhone USB
Install Apple Devices/iTunes drivers, enable Personal Hotspot, plug in, tap Trust. Select *iPhone Personal Hotspot*, Connect. Expect the Apple Ethernet adapter used, verification passes, speeds shown, the UI states that no CiciPDA tunnel/encryption and no Android companion are involved. Adapter present but hotspot off: the message explains the missing step.

### 18. iPhone Personal Hotspot over Wi-Fi
Join the hotspot from the Windows Wi-Fi menu, select the same method, Connect. Expect verification to pass; with Ethernet also connected expect the route check to fail with the instruction to unplug/disable the other adapter (no silent switching).

### 19. Clean disconnect and network restoration
After each scenario above: Disconnect, then check `ipconfig /all` (no CiciPDA adapter), `route print`, `Get-DnsClientServerAddress`, `netsh interface portproxy show all` (unchanged), hotspot state, `adb forward --list` (no CiciPDA ports), phone notification gone. The Activity log must contain the line "Rollback verified". Repeat after: closing the app while connected (Quit dialog), unplugging USB, killing the process, Windows sleep/resume, Windows restart.

## Sign-off
V1.0 requires: all 19 cases passed on Windows 10 **and** 11, at least two Android vendors across Android 8-15, one iPhone, release installer from the GitHub release, and no open High findings.
