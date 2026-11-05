$ErrorActionPreference = 'Stop'
$t = Join-Path $PSScriptRoot 'tools'
New-Item -ItemType Directory $t -Force | Out-Null
$tmp = Join-Path $env:TEMP 'cici-tools'
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $tmp | Out-Null
Invoke-WebRequest https://github.com/xjasonlyu/tun2socks/releases/download/v2.7.0/tun2socks-windows-amd64.zip -OutFile "$tmp\t2s.zip"
Invoke-WebRequest https://www.wintun.net/builds/wintun-0.14.1.zip -OutFile "$tmp\wintun.zip"
Expand-Archive "$tmp\t2s.zip" "$tmp\t2s"
Expand-Archive "$tmp\wintun.zip" "$tmp\wintun"
Copy-Item (Get-ChildItem "$tmp\t2s" -Filter *.exe -Recurse | Select-Object -First 1).FullName "$t\tun2socks.exe" -Force
Copy-Item "$tmp\wintun\wintun\bin\amd64\wintun.dll" "$t\wintun.dll" -Force
