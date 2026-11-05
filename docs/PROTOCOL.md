# Tunnel protocol v3 and its limits

This document describes what the code in `src/agent` (Android), `src/core/Tunnel.cs` and `src/core/Companion.cs` actually does.

## Transport
`adb forward tcp:0 tcp:<remotePort>` maps a free local TCP port to the companion's port. The companion listens on `127.0.0.1` only; the Windows SOCKS relay and the adb-forward endpoint are also loopback only. No inbound firewall rule is added.

## Session key (there is no long-lived pairing secret)
| Step | What happens |
| --- | --- |
| 1. PC | On every Connect a fresh random 32-byte **session key** is generated (`CompanionManager.NewSessionKey`). It is never written to disk. Any `pairing.key` left by a pre-2.1.1 version is deleted. Its hex form is registered with the logger so it is masked in every log line and error message. |
| 2. PC -> phone | The PC runs `adb -s <serial> shell` with a **fixed** argument list and sends the command through the process's **stdin**: `am start-foreground-service -n app.cicibyte.cicipda/.RelayService -a ...START --es key <hex> --ei port <n>`. The key therefore never appears in the PC's process command line. |
| 3. Phone shell | The phone's shell executes `am ...`. **For the duration of that command the key is part of the `am` process arguments and of the Intent extra on the phone**; it is readable only by the shell/system/root (same trust level as USB debugging itself). Ordinary apps cannot read other processes' command lines on Android 7+, and cannot send the intent (next row). |
| 4. Service | `RelayService` is `exported` but requires the caller to hold `android.permission.DUMP`, which only the adb shell (and system) holds. Ordinary apps cannot start, stop, re-pair or unpair it. No other component (no receiver, provider or second service) can receive the key. |
| 5. Storage on the phone | The key is wrapped with a **non-exportable AES-256-GCM key in the Android Keystore** (`PairingStore`) and the ciphertext is stored in app-private preferences. `android:allowBackup="false"`, so it is not backed up. It exists only so a system restart of the sticky service can resume the *current* session. The key is zeroed in memory after the relay copies it. |
| 6. End of session | `ACTION_STOP` (sent on every normal disconnect or failure) and `ACTION_UNPAIR` (Devices page, "Unpair companion") stop the relay **and delete the stored ciphertext and the Keystore wrapper**. A malformed key, a failed start or a refused foreground promotion do the same. The next Connect uses a brand-new key. |
| 7. Abandoned session | If the PC disappears without sending STOP (USB pulled, app killed), a watchdog ends the session after 180 s without an authenticated connection (the PC polls every 5 s while connected), deleting the key. |

What is **not** protected: someone who already controls the adb shell or root on the phone can read the key during a session. That actor can already do everything the companion does.

The legacy no-install helper (`app_process`, only when "fall back to the shell helper" is enabled) receives the key on **its own stdin** (a pipe inside `adb shell`), never in argv, and exits when stdin closes.

## Per-connection keys and framing
Each TCP connection to the relay performs: client sends 16 random bytes (`clientSalt`); relay replies with 16 random bytes (`serverNonce`); both derive
`kC2S = HMAC-SHA256(sessionKey, "c2s" || clientSalt || serverNonce)` and `kS2C` likewise with `"s2c"`.
Frames are `u16 length` + `AES-256-GCM(ciphertext || 16-byte tag)` with nonce = 4 zero bytes + 64-bit counter that only increases per direction. Reordered, dropped, replayed or modified frames fail authentication and close the connection; a recorded session cannot be replayed because the relay contributes fresh randomness. Maximum plaintext per frame: 15000 bytes. Only HMAC-SHA256 and AES-GCM are used.

## Commands (first client frame)
| Command | Meaning |
| --- | --- |
| `T:host:port` | TCP stream from the phone to host:port (IPv4/IPv6 literal or hostname; the phone resolves names) |
| `U` | UDP datagram relay; frames `family(4/6) | address | port | payload` in both directions |
| `S` | Authenticated status JSON: protocol, version, uptime, counters, phone network (transport, validated, VPN, metered), SDK, model |

## What is and is not carried
* **TCP**: yes, IPv4 and IPv6 destinations (SOCKS5 CONNECT from tun2socks).
* **UDP**: yes, as datagrams via SOCKS5 UDP ASSOCIATE (QUIC, games, voice). Best effort like any UDP. IP fragments are dropped; datagrams over about 14.9 KB are dropped; domain-name UDP targets are not produced by tun2socks and are dropped.
* **DNS**: Windows is configured (on the CiciPDA adapter only) to use the DNS servers from Settings (default 1.1.1.1 / 1.0.0.1). Those queries are ordinary **UDP/53 datagrams relayed through the phone**; there is no local DNS proxy and no DNS-over-HTTPS. The connection check sends a real UDP DNS query to the configured server and reports it separately.
* **ICMP**: **not carried.** `ping`/`tracert` to remote hosts do not work through the tunnel; the app's latency and "probe loss" are TCP-connect probes through the phone.
* **IPv6**: with "IPv6 leak protection" on, CiciPDA adds an IPv6 address and `::/1` + `8000::/1` routes on the adapter and *reports whether they were really installed*. Whether IPv6 works end to end depends on the phone's network; when it does not, applications fall back to IPv4. With the setting off, IPv6 traffic of a PC that has IPv6 may bypass the tunnel (the UI says so).
* Not a layer-3 VPN and not WireGuard. iPhone and hotspot-join modes use no tunnel and are not encrypted by CiciPDA.
* Android may stop a foreground service at any time (battery managers, memory pressure). The PC detects this through the 5-second status poll and reconnects with a new session key.

## Verified how
Protocol behaviour (framing, wrong key rejection, TCP/UDP/IPv6/status, restart with the same key, large datagrams, SOCKS relay) is covered by automated tests that run the **real Java relay on the PC** over loopback. The Android service code (`RelayService`, `PairingStore`, Keystore wrapping, watchdog) compiles into the signed APK but has **not been run on an Android device**.
