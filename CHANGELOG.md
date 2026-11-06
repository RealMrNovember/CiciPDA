# Changelog

## Website
- New project website at **pda.cicibyte.com** (source in `site/`) with a download button that follows the latest GitHub release; hosted on CiciByte's own server. No application change.

## 3.0.0 - What's new (includes everything from 2.1.x - 2.2.x)

**Important fix: the app no longer closes itself**
- In 2.1.7-2.2.0 pressing Connect (when the attempt failed) or Disconnect could close CiciPDA completely. Cause: stopping the packet engine sent a Ctrl+Break to its console, and CiciPDA was attached to that console and received it too. A real handler now absorbs it (verified with a GUI-process test harness).

**Runs in the system tray**
- While a connection is active (or connecting), the **X button hides CiciPDA to the system tray** instead of closing it, so the connection keeps working. The tray icon has *Open CiciPDA*, *Disconnect* and *Quit CiciPDA*; left-click opens the window. Starting CiciPDA again brings the running one to the front. Windows shutdown/sign-out, the updater and *Quit* still exit cleanly and restore your network.

**Remembers where you put the window**
- Position, size and maximised state are saved and restored on the next start (validated against the current monitors, so it never opens off-screen).

**Wi-Fi handover for sharing**
- If your PC is on Wi-Fi, CiciPDA can disconnect that network while the phone connection is used and rejoin it afterwards. It happens automatically when Wi-Fi sharing is on (the radio is needed for the hotspot) and can be enabled separately in Settings. It is journaled, so a crash or restart rejoins your network too. Never used when the phone is reached over Wi-Fi.

**See and manage the devices on your hotspot**
- *Devices* page: every device joined to your hotspot with its name, IP and MAC address and how long it has been connected; give each a name you choose (remembered), copy IP/MAC, and **Change password and disconnect all** (Windows has no per-device block or kick for Mobile Hotspot, so a new password is the supported way to remove devices).

## 2.2.0 - What's new

**DNS fix (the "no internet although connected" problem)**
- While connected, Windows now resolves names **only through the tunnel's DNS servers**. Windows normally sends every lookup to *all* adapters' DNS servers at once and uses the first answer, so an empty answer from another adapter produced *"The requested name is valid, but no data of the requested type was found"* and made all three internet checks fail. CiciPDA adds a temporary catch-all DNS policy rule named "CiciPDA" and removes it on disconnect, on exit and by crash recovery (switchable in Settings: *Resolve all names only through the tunnel*).
- The DNS cache is flushed once and DNS is retried before it counts as failed.
- If all internet checks still fail, the error shows an isolation report: direct TCP to 1.1.1.1:443, a UDP DNS query to the configured server, relay and adapter counters.

**Wi-Fi sharing is now visible**
- New *Wi-Fi sharing* card on the Connect page: on/off switch, network name, masked password with Show/Copy, state in plain language, connected-device count and Start/Stop buttons.

**Updates**
- The header **Update** pill shows download percentage, a progress bar, *Verifying* and *Installing*, and is disabled while busy so it cannot be pressed twice.
- New **Check for updates** button in the header.

**Reliability**
- A failed connection no longer blocks the next one (leftover adapter is reused; tun2socks is stopped gracefully).
- Fixed the tun2socks start options and device scheme, the installer's first-run launch (error 740), the missing desktop shortcut, and the Android companion's logo/screen.

Not yet validated end to end on physical devices; see `docs/HARDWARE_TEST_PLAN.md`.
