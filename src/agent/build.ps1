$ErrorActionPreference = 'Stop'
$jbr = 'C:\Program Files\Android\Android Studio\jbr'
$sdk = "$env:USERPROFILE\Android\Sdk"
$bt = Get-ChildItem "$sdk\build-tools" | Sort-Object Name | Select-Object -Last 1
$jar = (Get-ChildItem "$sdk\platforms\*\android.jar" | Select-Object -Last 1).FullName
$out = Join-Path $PSScriptRoot 'out'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $out | Out-Null
$env:JAVA_HOME = $jbr
& "$jbr\bin\javac.exe" --release 8 -d $out "$PSScriptRoot\TunnelMain.java"
& "$($bt.FullName)\d8.bat" --release --min-api 21 --lib $jar --output $out (Get-ChildItem $out -Recurse -Filter *.class).FullName
Copy-Item "$out\classes.dex" "$PSScriptRoot\..\app\agent.dex" -Force

