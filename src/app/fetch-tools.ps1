# Downloads the two third-party runtime tools into src\app\tools. Review the upstream projects before running.
#   wintun.dll     https://www.wintun.net            (WireGuard LLC, prebuilt signed driver DLL)
#   tun2socks.exe  https://github.com/xjasonlyu/tun2socks
$ErrorActionPreference = 'Stop'
$t = Join-Path $PSScriptRoot 'tools'
New-Item -ItemType Directory $t -Force | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('cici-tools-' + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
    Invoke-WebRequest 'https://github.com/xjasonlyu/tun2socks/releases/download/v2.7.0/tun2socks-windows-amd64.zip' -OutFile "$tmp\t2s.zip"
    Invoke-WebRequest 'https://www.wintun.net/builds/wintun-0.14.1.zip' -OutFile "$tmp\wintun.zip"
    Expand-Archive "$tmp\t2s.zip" "$tmp\t2s"
    Expand-Archive "$tmp\wintun.zip" "$tmp\wintun"
    Copy-Item (Get-ChildItem "$tmp\t2s" -Filter *.exe -Recurse | Select-Object -First 1).FullName "$t\tun2socks.exe" -Force
    Copy-Item "$tmp\wintun\wintun\bin\amd64\wintun.dll" "$t\wintun.dll" -Force
    $expected = 'e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce'   # wintun 0.14.1 amd64
    $actual = (Get-FileHash "$t\wintun.dll" -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected) { throw "wintun.dll hash mismatch: $actual" }
    Get-FileHash "$t\tun2socks.exe" -Algorithm SHA256 | Format-List
    Write-Host 'Tools installed in' $t
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
