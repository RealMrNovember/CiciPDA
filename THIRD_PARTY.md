# Third-party components and licensing checklist

CiciPDA redistributes (or downloads) the components below. **Review each licence before shipping a public installer; this list is an engineering checklist, not legal advice.**

| Component | Used for | Licence (upstream) | Distribution notes |
| --- | --- | --- | --- |
| Wintun `wintun.dll` 0.14.1 (WireGuard LLC) | Virtual network adapter | "Prebuilt Binaries License" (see wintun.net) | Redistribution of the unmodified prebuilt DLL with an application is permitted under that licence; do not modify it, keep the licence text with it, and do not distribute the driver separately. |
| tun2socks v2.7.0 (xjasonlyu) | Packet engine for the adapter | **GPL-3.0** | The installer ships the unmodified executable as a separate program invoked through a process boundary. GPL-3.0 still requires shipping the licence text and offering the corresponding source (link to the exact release tag). Decide whether this is acceptable for your distribution model before publishing. |
| Android Debug Bridge (`adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`) (Google) | Device discovery, install, port forwarding | Apache-2.0 (AOSP) / Android SDK terms for the packaged platform-tools | Check the current Android SDK licence terms for redistributing platform-tools binaries; if in doubt, download platform-tools from Google at install time instead of bundling. |
| .NET 9 runtime (self-contained publish) | Application runtime | MIT | Include the MIT notice. |
| Android companion (this repository) | Phone-side relay | Project licence (not yet chosen — add a `LICENSE` file) | Built from `src/agent`; signed with the release key. |

The companion APK and its release metadata are signed with keys that are **not** stored in this repository (`release/` is git-ignored). Keys embedded in the repository's `Pins.cs` are public pins only. The pins currently committed belong to the DEVELOPMENT keys and must be replaced for a public release (see docs/RELEASE.md).
