# Validates the REAL build outputs, not just that the project compiles:
#   1. artifacts\publish contains every runtime file
#   2. companion metadata matches the shipped APK; update.json matches the installer
#   3. the installer is run (per-user, silent, into a temp folder), every file is checked inside the INSTALLED copy
#      (adb hash must equal third_party\adb), then the uninstaller is run and must remove the folder
# Exit code 0 only if everything passes. Usage: validate-release.ps1 [-Version 2.1.0]
param([string]$Version = '2.1.8')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$art = Join-Path $root 'artifacts'
$pub = Join-Path $art 'publish'
$fail = New-Object System.Collections.Generic.List[string]
function Check($ok, $msg) { if ($ok) { Write-Host "  ok   $msg" } else { Write-Host "  FAIL $msg" -ForegroundColor Red; $script:fail.Add($msg) } }

$required = 'CiciPDA.exe', 'adb\adb.exe', 'adb\AdbWinApi.dll', 'adb\AdbWinUsbApi.dll', 'tools\wintun.dll', 'tools\tun2socks.exe', 'agent.dex',
            'companion\cicipda-companion.apk', 'companion\companion.json', 'companion\companion.json.sig'
Write-Host '== publish folder'
foreach ($f in $required) { Check (Test-Path (Join-Path $pub $f)) "publish\$f" }
foreach ($f in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll') {
    $a = Join-Path $root "third_party\adb\$f"; $b = Join-Path $pub "adb\$f"
    Check ((Test-Path $b) -and ((Get-FileHash $a).Hash -eq (Get-FileHash $b).Hash)) "publish\adb\$f identical to third_party\adb\$f"
}
Check (-not (Test-Path (Join-Path $pub 'data'))) 'no data\ directory in publish'

Write-Host '== installer script'
$iss = Get-Content (Join-Path $PSScriptRoot 'CiciPDA.iss') -Raw
Check ($iss -match [regex]::Escape('DefaultDirName={autopf}\CiciByte\CiciPDA')) 'default install folder is {autopf}\CiciByte\CiciPDA (C:\Program Files\CiciByte\CiciPDA)'

# Regression guard for the 2.1.1 first-run failure (error 740: CreateProcess cannot start an exe flagged RUNASADMIN).
$runLine = ($iss -split "`n" | Where-Object { $_ -match '^Filename: "\{app\}\\CiciPDA.exe"' } | Select-Object -First 1)
Check ($runLine -match 'shellexec') 'post-install launch uses shellexec (UAC prompt, no error 740)'
Check ($iss -match 'RUNASADMIN' -and $runLine -match 'postinstall') 'run-as-admin flag and post-install launch both present (so the shellexec check matters)'

Write-Host '== release assets'
$meta = Get-Content (Join-Path $art 'companion.json') -Raw | ConvertFrom-Json
Check ((Get-FileHash (Join-Path $art 'cicipda-companion.apk') -Algorithm SHA256).Hash.ToLower() -eq $meta.sha256) 'companion.json sha256 matches cicipda-companion.apk'
Check (Test-Path (Join-Path $art 'companion.json.sig')) 'companion.json.sig present'
$upd = Get-Content (Join-Path $art 'update-windows-x64.json') -Raw | ConvertFrom-Json
$setup = Join-Path $art "CiciPDA-$Version-windows-x64-Setup.exe"
Check (Test-Path $setup) "installer CiciPDA-$Version-windows-x64-Setup.exe built"
if (Test-Path $setup) {
    Check ((Get-FileHash $setup -Algorithm SHA256).Hash.ToLower() -eq $upd.sha256) 'update-windows-x64.json sha256 matches the installer'
    Check ($upd.version -eq $Version) 'update-windows-x64.json version matches'
    Check (Test-Path (Join-Path $art 'update-windows-x64.json.sig')) 'update-windows-x64.json.sig present'

    Write-Host '== install into a temp folder (per-user, silent)'
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('CiciByte-validate-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $log = Join-Path $art 'install-validate.log'
    $p = Start-Process $setup -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /NOICONS /DIR=`"$dir`" /LOG=`"$log`"" -Wait -PassThru
    Check ($p.ExitCode -eq 0) "installer exit code 0 (was $($p.ExitCode))"
    foreach ($f in $required + 'licenses\THIRD_PARTY.md', 'licenses\LICENSE') { Check (Test-Path (Join-Path $dir $f)) "installed: $f" }
    foreach ($f in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll') {
        $b = Join-Path $dir "adb\$f"
        Check ((Test-Path $b) -and ((Get-FileHash (Join-Path $root "third_party\adb\$f")).Hash -eq (Get-FileHash $b).Hash)) "installed adb\$f is the bundled one"
    }
    Check ((Get-FileHash (Join-Path $dir 'companion\cicipda-companion.apk') -Algorithm SHA256).Hash.ToLower() -eq $meta.sha256) 'installed APK matches signed metadata hash'
    # desktop shortcut (was missing in 2.1.1: the task was optional and unchecked)
    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'CiciPDA.lnk'
    Check (Test-Path $lnk) 'desktop shortcut CiciPDA.lnk created'
    if (Test-Path $lnk) {
        $target = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath
        Check ($target -eq (Join-Path $dir 'CiciPDA.exe')) 'desktop shortcut points to the installed CiciPDA.exe'
    }
    $un = Join-Path $dir 'unins000.exe'
    Check (Test-Path $un) 'uninstaller present'
    if (Test-Path $un) {
        Start-Process $un -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait | Out-Null
        for ($i = 0; $i -lt 20 -and (Test-Path (Join-Path $dir 'CiciPDA.exe')); $i++) { Start-Sleep -Milliseconds 500 }
        Check (-not (Test-Path (Join-Path $dir 'CiciPDA.exe'))) 'uninstall removed the application'
        Check (-not (Test-Path $lnk)) 'uninstall removed the desktop shortcut'
    }
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fail.Count) { Write-Host "`nVALIDATION FAILED ($($fail.Count))" -ForegroundColor Red; exit 1 }
Write-Host "`nVALIDATION PASSED"
