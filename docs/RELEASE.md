# Release process

Publisher: **CiciByte Technology, Inc.** (https://cicibyte.com). Installer default folder: `C:\Program Files\CiciByte\CiciPDA`.

## Platforms
| Platform | Release assets | Status |
| --- | --- | --- |
| **Windows x64** | `CiciPDA-<version>-windows-x64-Setup.exe`, `update-windows-x64.json` (+ `.sig`) | Supported |
| **Android** (companion for the Windows app) | `cicipda-companion.apk`, `companion.json` (+ `.sig`) | Supported; installed automatically from Windows |
| **Linux** | none | **Not available.** The app depends on WPF, Wintun and `netsh`; no Linux build exists |
| **macOS** | none | **Not available.** Same reason |

Asset names carry the platform so future ports can be added side by side (`...-linux-x64...`, `...-macos-arm64...`) without renaming. `update.json` / `update.json.sig` are published as a **legacy alias** (identical bytes) so already-installed 2.1.0 clients, which read that name, can still update.

## Keys (back them up offline)
All private material is in the git-ignored `release/` folder on the maintainer machine:
* `release/prod.keystore` + `release/prod.pass` - Android APK signing key (RSA 4096, alias `cicipda-release`). **If it is lost, installed companions can never be updated in place** (Android refuses another signer).
* `release/prod-meta/private.pk8` - ECDSA P-256 key signing `companion.json` and the update manifests. If lost or leaked, follow a documented rotation: generate a new pair with `ReleaseSigner genkey`, regenerate `src/core/Pins.cs`, ship a new installer (old apps will not trust the new key until updated manually).
* Public pins are compiled into the app (`src/core/Pins.cs`, `IsDevelopment = false`). They have not been changed.
* `release/dev.keystore` and `release/meta` are throw-away development keys, not trusted by production builds.

`ReleaseSigner` (`tools/ReleaseSigner`): `genkey <dir>`, `sign <file> <private.pk8>`, `pins <public.spki> <apk> <Pins.cs> [--dev]`.

## Cutting a release
1. Set the version in `src/app/CiciPDA.csproj` (`<Version>`), `installer/CiciPDA.iss`, `installer/build-installer.ps1`, and, if the phone side changed, `RelayService.VERSION` and `src/agent/build.ps1 -VersionName/-VersionCode`.
2. `dotnet test tests/CiciPDA.Tests`.
3. `installer/build-installer.ps1 -Version X.Y.Z -Notes "what changed"` rebuilds the companion (signed with `release/prod.keystore`) unless `-SkipCompanion`, publishes the app self-contained, compiles the installer (Inno Setup 6; fails if compilation fails), writes **signed** `update-windows-x64.json(.sig)` (+ legacy `update.json(.sig)`), then runs `installer/validate-release.ps1`, which checks the real publish folder, the release assets, **installs the installer per-user into a temp folder, verifies every installed file, and uninstalls it**. After a companion rebuild, sign `src/app/companion/companion.json` with `ReleaseSigner sign` before building the installer.
4. Create a GitHub release `vX.Y.Z` and attach from `artifacts/`: the Setup exe, `update-windows-x64.json(.sig)`, `update.json(.sig)`, `companion.json(.sig)`, `cicipda-companion.apk`.
5. Verify the live channel: `CICI_LIVE=1 dotnet test --filter LiveUpdateChannel` (the updater must be offered the new version, nothing when current, and the real installer must download and pass the signed checksum).

## How clients update
* **Windows app:** fetches `releases/latest/download/update-windows-x64.json` + `.sig`, verifies the signature with the pinned key, compares versions, downloads the installer only from the pinned GitHub host, checks the signed SHA-256, asks the user, closes the connection and restores network state, then runs the installer silently (`/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`).
* **Android companion:** same channel; metadata signature, APK hash and pinned signer certificate are verified before `adb install -r`; Android re-verifies the signature.
* Installs made by 2.1.0 keep their original folder (`...\CiciByte`) when updated, because Inno Setup reuses the previous directory.

## Release blockers (not done)
* No physical-device validation (HARDWARE_TEST_PLAN.md). **V1.0 must not be declared until it is complete.**
* No Authenticode certificate: SmartScreen warns on first run.
* Licence review items in `THIRD_PARTY.md` (tun2socks GPL-3.0 source offer, adb redistribution terms).
* The in-app "Update" button has not been exercised end to end (needs a newer release than the installed one, plus UAC).

## Website (pda.cicibyte.com)

`site/` is deployed to GitHub Pages by `.github/workflows/pages.yml` on every push to `master` that touches it. Releases need no website change: `site/app.js` reads the latest release from the GitHub API and points the download button at the `*-windows-x64-Setup.exe` asset (falling back to the releases page). DNS: `CNAME pda -> realmrnovember.github.io`; repository Settings -> Pages must use "GitHub Actions" as the source with custom domain `pda.cicibyte.com` and "Enforce HTTPS".
