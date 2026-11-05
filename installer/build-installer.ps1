# Produces artifacts\CiciPDA-Setup-<version>.exe
#  1. rebuild the companion (src\agent\build.ps1) unless -SkipCompanion
#  2. dotnet publish (self-contained, win-x64) so end users need no .NET
#  3. ISCC (Inno Setup 6) compiles installer\CiciPDA.iss
# Release signing: set CICI_KEYSTORE/CICI_KS_PASS/CICI_KS_ALIAS before step 1, run tools\ReleaseSigner to sign companion.json,
# and pass -SignTool "signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256" to sign the installer.
param([string]$Version = '2.1.0', [switch]$SkipCompanion, [string]$SignTool = '', [string]$Notes = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$pub = Join-Path $root 'artifacts\publish'
if (-not $SkipCompanion) { & "$root\src\agent\build.ps1" }
foreach ($f in 'src\app\tools\wintun.dll', 'src\app\tools\tun2socks.exe', 'src\app\companion\companion.json', 'src\app\companion\companion.json.sig') {
    if (-not (Test-Path "$root\$f")) { throw "Missing $f (run src\app\fetch-tools.ps1 / sign the companion metadata)." }
}
Remove-Item $pub -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\src\app\CiciPDA.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $pub
if ($LASTEXITCODE) { throw 'publish failed' }
$iscc = @("$env:ProgramFiles (x86)\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Warning "Inno Setup 6 not found. Published files are in $pub; install Inno Setup and re-run to create the installer."; exit 0 }
$args = @("/DAppVersion=$Version", "/DPublishDir=$pub")
if ($SignTool) { $args += "/Ssigntool=$SignTool" }
& $iscc @args "$PSScriptRoot\CiciPDA.iss"

# ---- signed update manifest (consumed by the in-app updater) ----
$setup = Join-Path $root "artifacts\CiciPDA-Setup-$Version.exe"
if (Test-Path $setup) {
    $sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
    $manifest = [ordered]@{ product = 'CiciPDA'; version = $Version; installer = (Split-Path $setup -Leaf); sha256 = $sha; size = (Get-Item $setup).Length; notes = $Notes }
    $mpath = Join-Path $root 'artifacts\update.json'
    [IO.File]::WriteAllText($mpath, ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
    $signer = Join-Path $root 'tools\ReleaseSigner\bin\Release\net9.0-windows\ReleaseSigner.dll'
    if (-not (Test-Path $signer)) { dotnet build "$root\tools\ReleaseSigner" -c Release -nologo -v q | Out-Null }
    $key = if ($env:CICI_META_KEY) { $env:CICI_META_KEY } else { Join-Path $root 'release\prod-meta\private.pk8' }
    dotnet $signer sign $mpath $key
    # companion assets for the same GitHub release
    Copy-Item "$root\src\app\companion\companion.json", "$root\src\app\companion\companion.json.sig", "$root\src\app\companion\cicipda-companion.apk" "$root\artifacts" -Force
    Write-Host "Release assets ready in $root\artifacts : $(Split-Path $setup -Leaf), update.json(.sig), companion.json(.sig), cicipda-companion.apk"
}
