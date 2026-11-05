# Builds the phone-side components:
#   out\classes          relay classes (used by the Windows loopback tests)
#   ..\app\agent.dex     legacy app_process launcher (fallback when the companion APK is absent)
#   ..\app\companion\    cicipda-companion.apk (signed) + companion.json (unsigned release metadata)
# Requires: Android SDK build-tools + platform, and a JDK (Android Studio JBR is used if present).
# Signing: set CICI_KEYSTORE / CICI_KS_PASS / CICI_KS_ALIAS for a release build; otherwise a local DEV keystore is created
# in ..\..\release (git-ignored). Dev-signed APKs must never be published.
param([string]$VersionName = '2.0.0', [int]$VersionCode = 20000)
$ErrorActionPreference = 'Stop'
$jbr = if (Test-Path 'C:\Program Files\Android\Android Studio\jbr') { 'C:\Program Files\Android\Android Studio\jbr' } else { $env:JAVA_HOME }
$env:JAVA_HOME = $jbr
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { "$env:USERPROFILE\Android\Sdk" }
$bt = (Get-ChildItem "$sdk\build-tools" | Sort-Object { [version]($_.Name -replace '[^\d\.]','') } | Select-Object -Last 1).FullName
$jar = (Get-ChildItem "$sdk\platforms\*\android.jar" | Sort-Object FullName | Select-Object -Last 1).FullName
$root = $PSScriptRoot
$out = Join-Path $root 'out'
$appDir = Join-Path $root '..\app'
$comp = Join-Path $appDir 'companion'
$rel = Join-Path $root '..\..\release'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$out\classes", "$out\apkclasses", "$out\apk", $comp, $rel -Force | Out-Null

# 1) relay + legacy launcher
$relaySrc = Get-ChildItem "$root\src" -Recurse -Filter *.java | ForEach-Object FullName
& "$jbr\bin\javac.exe" --release 8 -Xlint:-options -d "$out\classes" $relaySrc
if ($LASTEXITCODE) { throw 'javac (relay) failed' }
$cls = Get-ChildItem "$out\classes" -Recurse -Filter *.class | ForEach-Object FullName
& "$bt\d8.bat" --release --min-api 21 --lib $jar --output "$out" $cls
if ($LASTEXITCODE) { throw 'd8 (legacy) failed' }
Copy-Item "$out\classes.dex" "$appDir\agent.dex" -Force

# 2) companion APK
$apkSrc = @($relaySrc) + (Get-ChildItem "$root\android" -Filter *.java | ForEach-Object FullName)
& "$jbr\bin\javac.exe" --release 8 -Xlint:-options -cp $jar -d "$out\apkclasses" $apkSrc
if ($LASTEXITCODE) { throw 'javac (apk) failed' }
$acls = Get-ChildItem "$out\apkclasses" -Recurse -Filter *.class | ForEach-Object FullName
& "$bt\d8.bat" --release --min-api 24 --lib $jar --output "$out\apk" $acls
if ($LASTEXITCODE) { throw 'd8 (apk) failed' }
& "$bt\aapt2.exe" link -o "$out\base.apk" -I $jar --manifest "$root\android\AndroidManifest.xml" --min-sdk-version 24 --target-sdk-version 34 --version-code $VersionCode --version-name $VersionName
if ($LASTEXITCODE) { throw 'aapt2 failed' }
Push-Location "$out\apk"; & "$bt\aapt.exe" add "$out\base.apk" classes.dex | Out-Null; Pop-Location
& "$bt\zipalign.exe" -f -p 4 "$out\base.apk" "$out\aligned.apk"
if ($LASTEXITCODE) { throw 'zipalign failed' }

# 3) sign
if ($env:CICI_KEYSTORE) { $ks = $env:CICI_KEYSTORE; $pass = $env:CICI_KS_PASS; $alias = $env:CICI_KS_ALIAS }
elseif (Test-Path (Join-Path $rel 'prod.keystore')) {
    $ks = Join-Path $rel 'prod.keystore'; $alias = 'cicipda-release'; $pass = (Get-Content (Join-Path $rel 'prod.pass') -Raw).Trim()
}
else {
    $ks = Join-Path $rel 'dev.keystore'; $alias = 'cicipda-dev'; $pass = 'cicipda-dev-only'
    if (-not (Test-Path $ks)) {
        & "$jbr\bin\keytool.exe" -genkeypair -keystore $ks -storepass $pass -keypass $pass -alias $alias -keyalg RSA -keysize 3072 -validity 9125 -dname 'CN=CiciPDA Dev (do not publish)'
        if ($LASTEXITCODE) { throw 'keytool failed' }
    }
    Write-Warning 'Signing with the local DEV keystore. This APK must not be published.'
}
$apk = Join-Path $comp 'cicipda-companion.apk'
& "$bt\apksigner.bat" sign --ks $ks --ks-pass "pass:$pass" --key-pass "pass:$pass" --ks-key-alias $alias --min-sdk-version 24 --out $apk "$out\aligned.apk"
if ($LASTEXITCODE) { throw 'apksigner failed' }
& "$bt\apksigner.bat" verify --print-certs --min-sdk-version 24 $apk | Out-Host

# 4) unsigned release metadata (signed separately by sign-release.ps1 with the offline release key)
$sha = (Get-FileHash $apk -Algorithm SHA256).Hash.ToLower()
$certLine = (& "$bt\apksigner.bat" verify --print-certs --min-sdk-version 24 $apk | Select-String 'certificate SHA-256 digest: (\w+)' | Select-Object -First 1).Matches[0].Groups[1].Value.ToLower()
$meta = [ordered]@{ package = 'app.cicibyte.cicipda'; versionName = $VersionName; versionCode = $VersionCode; protocol = 3; minSdk = 24; apk = 'cicipda-companion.apk'; sha256 = $sha; signerCertSha256 = $certLine }
[IO.File]::WriteAllText("$comp\companion.json", ($meta | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
Write-Host "Companion $VersionName ($VersionCode) sha256=$sha signer=$certLine"
