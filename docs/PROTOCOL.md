# Tunnel protocol v3 and its limits

Transport: `adb forward` maps a free local TCP port to the companion's loopback port. Nothing listens on a network interface on the phone (`127.0.0.1` only) or on Windows (`127.0.0.1` only).

## Pairing and keys
* The PC creates a random 32-byte **pairing key** once (`data/pairing.key`, Windows DPAPI-protected). It is sent to the phone over `adb shell` **stdin** when the service is started, never on a command line, and stored in the companion's private storage.
* Trust root: Android's USB-debugging authorization. Only the adb shell (`android.permission.DUMP`) can start, stop or re-pair the service; ordinary apps cannot.
* Each phone connection derives fresh keys: `kC2S/kS2C = HMAC-SHA256(pairingKey, label || clientSalt(16) || serverNonce(16))`. Both sides contribute randomness, so a recorded session cannot be replayed into a new connection.

## Framing
`u16 length` + `AES-256-GCM(ciphertext || tag)`; nonce = 4 zero bytes + 64-bit counter that only increases per direction. Reordered, dropped, replayed or modified frames fail authentication and close the connection. Maximum plaintext per frame: 15000 bytes. Only standard primitives (HMAC-SHA256, AES-GCM) are used; no custom cipher.

## Commands (first client frame)
| Command | Meaning |
| --- | --- |
| `T:host:port` | TCP stream to host:port from the phone (IPv4, IPv6 literal or name; the phone resolves names) |
| `U` | UDP datagram relay: frames `family(4/6) | address | port | payload` in both directions |
| `S` | Authenticated status JSON: protocol, version, uptime, connection counters, phone network transport/validated/VPN/metered, SDK, model |

## What is and is not tunnelled
* **TCP**: yes (IPv4 and IPv6 destinations).
* **UDP**: yes, as datagrams via SOCKS5 UDP ASSOCIATE (DNS, QUIC). IP fragments are dropped; datagrams larger than about 14.9 KB are dropped. Windows DNS goes to the configured server through the phone over UDP.
* **ICMP** (ping/tracert to remote hosts): **not** carried.
* This is a SOCKS-style connection relay fed by tun2socks, not a full layer-3 VPN and not WireGuard. UDP is best effort, like on any network.
* IPv6: the adapter and routes exist when "IPv6 leak protection" is on; whether IPv6 works end to end depends on the phone's network. Apps fall back to IPv4 when it does not.
* The phone side cannot be kept alive indefinitely by the app: it is a foreground service (visible notification). Aggressive OEM battery managers may still stop it; the PC detects this through the status poll and reconnects.
