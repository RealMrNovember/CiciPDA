# Changelog

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
