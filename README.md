<div align="center">

# CiciPDA

**Share your phone's internet with your Windows PC — from one desktop app.**

A Windows networking utility that connects an Android phone to a PC over USB, builds a virtual network adapter, routes Windows traffic through the phone's own connection, and shows what is happening in real time.

![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20(x64)-0b0f14?style=flat-square&logo=windows&logoColor=00e5a0)
![.NET](https://img.shields.io/badge/.NET-9%20%2F%20WPF-0b0f14?style=flat-square&logo=dotnet&logoColor=00e5a0)
![Status](https://img.shields.io/badge/status-active%20development-0b0f14?style=flat-square&color=00bcd4)

[**☕ Buy me a coffee**](https://buymeacoffee.com/MikeRoss)

</div>

---

## Overview

CiciPDA turns a phone into the uplink of a Windows machine. The goal is a single, polished application that handles the whole workflow:

1. Detect the phone and check that it is authorized.
2. Start a small helper on the phone and open a secured channel to it.
3. Create a virtual network adapter on Windows and route traffic through the channel.
4. **Verify** that DNS and HTTP/HTTPS really work before reporting a connection.
5. Monitor throughput, latency and data usage, and restore the network cleanly on disconnect.

The app never reports **Connected** unless the data path has actually passed its checks, and it does not fake Windows network-status (NCSI) results.

## Connection methods

| Method | What it does | Status |
| --- | --- | --- |
| **Phone VPN** (recommended) | Wintun adapter on Windows → authenticated, encrypted channel over ADB → helper on the phone. Traffic follows the phone's connection, including any VPN running on the phone. | Implemented; awaiting on-device validation |
| **USB** | Same tunnel, restricted to a USB cable. Android devices are detected through ADB, including authorization state. | Implemented; awaiting on-device validation |
| **Android Wi-Fi hotspot** | Detects the Wi-Fi network you joined, verifies connectivity and monitors the interface. | Implemented (verify/monitor only); awaiting validation |
| **iPhone Personal Hotspot** | Detects the hotspot or Apple USB Ethernet adapter, verifies and monitors. No tunnel is created. | Implemented (verify/monitor only); awaiting validation |

Capabilities that are not implemented and verified are not presented as working in the interface.

## Planned feature set

- **Connect** — method cards, dynamic phone-preparation checklist, a single connection control with distinct disconnected / connecting / connected / error states.
- **Live Monitor** — download and upload speed, session totals, latency, probe loss, uptime, interface details and a traffic history chart, based on real counters.
- **Activity** — timestamped, severity-tagged event feed.
- **Connection History** — persisted sessions with method, device, duration, data used and result.
- **Devices** — connected and previously paired devices, trust status, last seen, management actions.
- **Network Diagnostics** — device, ADB, adapter, default route, DNS, HTTP/HTTPS and Windows connectivity tests with suggested fixes.
- **Settings** — transport, auto-connect and reconnect, DNS, data-usage alerts, logging level, theme and language.
- **Optional Wi-Fi sharing** from the PC through Windows Internet Connection Sharing.
- A persistent status bar with connection state, speeds, data usage and time.

Visual direction: a dark, near-black interface with restrained green/cyan accents, thin borders and subtle motion — a networking tool, not a generic dashboard.

## Architecture

```
┌────────────────────────── Windows ──────────────────────────┐
│  WPF UI  ─►  Connection orchestrator (state machine)        │
│                 │                                           │
│   Device discovery (ADB) · Routing/DNS · Diagnostics        │
│   Traffic monitor · History/Settings store · Logging        │
│                 │                                           │
│   Wintun adapter ◄─► tun2socks ◄─► local SOCKS5 relay       │
└─────────────────────────────┬───────────────────────────────┘
                              │  ADB forward (USB)
                              │  AES-256-GCM framed channel
┌─────────────────────────────▼───────────────────────────────┐
│  Android helper (app_process, no root) ─► phone's network   │
└─────────────────────────────────────────────────────────────┘
```

- **Wintun + tun2socks** provide the virtual adapter and packet handling instead of custom driver code.
- **Standard primitives only** — AES-GCM with per-connection derived keys; no home-made cryptography. The session key is random per connection and is passed to the helper over stdin, never on a command line and never logged.
- **Reversible by design** — every route, DNS and sharing change is journaled and undone on disconnect, on exit and after a crash.
- **Explicit routing** — split default routes through the tunnel; the transport itself is excluded to prevent routing loops and leaks.

## Requirements

- Windows 10 or 11, 64-bit
- An Android phone with **USB debugging** enabled, and a data-capable USB cable
- Administrator rights for the connection step (virtual adapter, routes and DNS)
- [.NET 9 SDK](https://dotnet.microsoft.com/download) to build from source

## Build

```powershell
cd src\app
.\fetch-tools.ps1      # downloads wintun.dll and tun2socks.exe into tools\
dotnet build -c Release
dotnet test ..\..\tests\CiciPDA.Tests
```

See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for architecture, audit findings, test results and what still needs hardware validation.

Runtime tools that are not committed to the repository:

- `wintun.dll` — from [wintun.net](https://www.wintun.net)
- `tun2socks.exe` — from [xjasonlyu/tun2socks](https://github.com/xjasonlyu/tun2socks)

`src/app/fetch-tools.ps1` downloads both into `src/app/tools` (and checks the `wintun.dll` hash). Review the script and the upstream releases before running it.

The Android helper source is `src/agent/TunnelMain.java`; `src/agent/build.ps1` rebuilds `agent.dex` with the Android SDK build-tools.

## Privacy and security

- Settings and history are stored locally in the `data` folder; nothing is sent anywhere by the app itself.
- Secrets (for example hotspot passwords) are stored with Windows DPAPI, never in plain text or logs.
- All external commands are launched with explicit argument lists; inputs are validated.
- CiciPDA is intended for legitimate connectivity. It does **not** attempt to disguise traffic or evade carrier policies. Please use it only with your own phone and within the terms of your mobile plan.

## Project status

The application is under active development. Features that need a physical phone, a USB cable, a Wi-Fi adapter or administrator privileges are validated on real hardware and documented as such before being marked complete.

## Support the project

If CiciPDA is useful to you, you can support its development:

<a href="https://buymeacoffee.com/MikeRoss"><img src="https://img.shields.io/badge/Buy%20me%20a%20coffee-MikeRoss-00e5a0?style=for-the-badge&logo=buymeacoffee&logoColor=black" alt="Buy me a coffee"></a>

## Acknowledgements

Built on [Wintun](https://www.wintun.net), [tun2socks](https://github.com/xjasonlyu/tun2socks) and Google's Android Debug Bridge. Inspired by the PdaNet idea; CiciPDA is an independent project and is not affiliated with PdaNet. All trademarks belong to their owners.
