# Release process

## Keys (already created on 2026-11-06; **back them up offline**)
All private material is in the git-ignored `release/` folder on the maintainer machine:
* `release/prod.keystore` + `release/prod.pass` - Android APK signing key (RSA 4096, alias `cicipda-release`). **If this key is lost, installed companions can never be updated in place** (Android refuses a different signer) and users must uninstall first.
* `release/prod-meta/private.pk8` - ECDSA P-256 key that signs `companion.json` and `update.json`. If lost or leaked, rotate: generate a new pair with `ReleaseSigner genkey`, regenerate `src/core/Pins.cs`, and ship a new installer (old apps will not trust the new key until updated manually).
* The public pins are compiled into the app (`src/core/Pins.cs`, `IsDevelopment = false`).
* `release/dev.keystore` and `release/meta` are throw-away development keys and are not trusted by production builds.

`ReleaseSigner` (`tools/ReleaseSigner`): `genkey <dir>`, `sign <file> <private.pk8>`, `pins <public.spki> <apk> <Pins.cs> [--dev]`.

## Cutting a release
1. Set the version in `src/app/CiciPDA.csproj` (`<Version>`), `installer/CiciPDA.iss` default, and, if the phone side changed, `RelayService.VERSION` and `build.ps1 -VersionName/-VersionCode`.
2. `dotnet test tests/CiciPDA.Tests`.
3. `installer/build-installer.ps1 -Version X.Y.Z -Notes "what changed"`. This rebuilds the companion (signed with `release/prod.keystore`), publishes the app self-contained, compiles the installer with Inno Setup, and writes **signed** `update.json`/`update.json.sig` next to it in `artifacts/`. Add `-SignTool "signtool sign ..."` once you have an Authenticode certificate.
4. Create a GitHub release tagged `vX.Y.Z` and attach exactly these files from `artifacts/`: `CiciPDA-Setup-X.Y.Z.exe`, `update.json`, `update.json.sig`, `companion.json`, `companion.json.sig`, `cicipda-companion.apk`.

## How clients update
* **Windows app:** fetches `releases/latest/download/update.json` + `.sig`, verifies the signature with the pinned key, compares versions, downloads the installer only from the pinned GitHub host, checks the signed SHA-256, asks the user, then runs the installer silently (`/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`) after restoring network state.
* **Android companion:** same channel; metadata signature, APK hash and signer certificate (pinned) are verified before `adb install -r`; Android re-verifies the signature.

## Known release gaps
* No Authenticode certificate yet (SmartScreen warning). Obtain a code-signing certificate and pass `-SignTool`.
* Licence review items in `THIRD_PARTY.md` (tun2socks GPL-3.0 source offer, adb redistribution terms).
* The installer and update path have not been exercised on a clean machine against a real GitHub release.

## Supported Android versions
API 24 (Android 7.0) and newer by manifest. Tested on real devices: none yet. Android 13+ needs the user to allow notifications once; Android 14+ needs the foreground-service type declared in the manifest (`specialUse`).
