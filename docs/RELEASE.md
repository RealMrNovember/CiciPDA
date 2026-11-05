# Release process

## Keys (create once, store offline)
* **APK signing key** (Android keystore). Set `CICI_KEYSTORE`, `CICI_KS_PASS`, `CICI_KS_ALIAS` for release builds. Without them `build.ps1` uses a throw-away dev keystore in `release/` and warns.
* **Metadata signing key** (ECDSA P-256): `ReleaseSigner genkey <dir>`. Keep `private.pk8` offline.
* Publish the pins: `ReleaseSigner pins <public.spki> <companion.apk> src/core/Pins.cs` (without `--dev`) and commit `Pins.cs` (public values only). **The committed pins are DEVELOPMENT keys; a public release must replace them.**

`ReleaseSigner` lives in `tools/ReleaseSigner` (build it, then run `dotnet bin/Debug/net9.0-windows/ReleaseSigner.dll <command>`).

## Steps
1. Bump `-VersionName/-VersionCode` and `RelayService.VERSION`; run `src/agent/build.ps1`.
2. `ReleaseSigner pins ...` then `ReleaseSigner sign src/app/companion/companion.json <private.pk8>` to create `companion.json.sig`.
3. `dotnet test tests/CiciPDA.Tests` (the trust tests must pass).
4. `installer/build-installer.ps1 -Version X [-SignTool "..."]` creates `artifacts/CiciPDA-Setup-X.exe` (needs Inno Setup 6). Authenticode-sign the installer and `CiciPDA.exe`.
5. Create a GitHub release and attach **`companion.json`, `companion.json.sig`, `cicipda-companion.apk`** with exactly those names. Installed apps poll `releases/latest/download/...`, verify the metadata signature with the pinned key **before** downloading the APK, then verify hash and signer certificate, and only then give the APK to `adb install`.

## What a client checks
Signature of metadata (pinned ECDSA key), then APK SHA-256 equals the signed metadata, then the APK signer certificate equals both the metadata and the pin compiled into the app. Android then re-verifies the signature and refuses updates signed by another key. A hash served next to the APK without a valid metadata signature is ignored.

## Supported Android versions
API 24 (Android 7.0) and newer by manifest. Tested on real devices: none yet (see DEVELOPMENT.md). Android 13+ needs the user to allow notifications once; Android 14+ requires the foreground-service type declared in the manifest (`specialUse`).
