# Builds helper/adbview-input.dex (embedded into adbView.exe). Needs a JDK and the Android SDK.
$ErrorActionPreference = 'Stop'
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { "$env:LOCALAPPDATA\Android\Sdk" }
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = "$env:ProgramFiles\Android\Android Studio\jbr" }
$jar = Get-ChildItem "$sdk\platforms\android-*\android.jar" | Sort-Object FullName | Select-Object -Last 1
$d8 = Get-ChildItem "$sdk\build-tools\*\d8.bat" | Sort-Object FullName | Select-Object -Last 1
$out = Join-Path $PSScriptRoot 'obj'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $out | Out-Null
& "$env:JAVA_HOME\bin\javac.exe" --release 11 -cp $jar.FullName -d $out "$PSScriptRoot\AdbViewInput.java"
if ($LASTEXITCODE) { throw 'javac failed' }
& $d8.FullName --release --min-api 21 --lib $jar.FullName --output $out (Get-ChildItem "$out\*.class").FullName
if ($LASTEXITCODE) { throw 'd8 failed' }
Copy-Item "$out\classes.dex" "$PSScriptRoot\adbview-input.dex" -Force
Remove-Item $out -Recurse -Force
Write-Host "OK: helper\adbview-input.dex"
