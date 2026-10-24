<#
  Builds the PC Diagnostic companion app (android/dist/pcdiagnostic.apk), which the web host serves at
  /app/pcdiagnostic.apk. Needs JDK 17 and an Android SDK (platform 34, build-tools 34+); uses Gradle 8.7 from PATH
  or from the Gradle wrapper cache, and the Android SDK from ANDROID_HOME, ANDROID_SDK_ROOT or C:\Android.

  -Release signs with your release key instead of the debug key: set PCDIAG_KEYSTORE (path to the .jks),
  PCDIAG_KEYSTORE_PASSWORD and optionally PCDIAG_KEY_ALIAS (default pcdiagnostic) and PCDIAG_KEY_PASSWORD. Create
  the key once, outside the repository, and back it up (phones only accept updates signed with the same key):
    keytool -genkeypair -v -keystore $HOME\pcdiag-release.jks -alias pcdiagnostic -keyalg RSA -keysize 3072 -validity 10000
#>
[CmdletBinding()]
param([switch]$Offline, [switch]$Release)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$sdk = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, 'C:\Android', "$env:LOCALAPPDATA\Android\Sdk") | Where-Object { $_ -and (Test-Path (Join-Path $_ 'platforms')) } | Select-Object -First 1
if (-not $sdk) { throw 'No Android SDK found. Set ANDROID_HOME.' }
"sdk.dir=$($sdk.Replace('\', '\\').Replace(':', '\:'))" | Set-Content -Encoding ascii local.properties

$gradle = (Get-Command gradle -ErrorAction SilentlyContinue).Source
if (-not $gradle) {
    $gradle = Get-ChildItem "$env:USERPROFILE\.gradle\wrapper\dists\gradle-8.7-*\*\gradle-8.7\bin\gradle.bat" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $gradle) { throw 'Gradle 8.7 not found (install it or run any Gradle 8.7 wrapper once).' }

if ($Release -and -not ($env:PCDIAG_KEYSTORE -and (Test-Path $env:PCDIAG_KEYSTORE) -and $env:PCDIAG_KEYSTORE_PASSWORD)) {
    throw 'A release build needs PCDIAG_KEYSTORE (an existing keystore file) and PCDIAG_KEYSTORE_PASSWORD.'
}
$arguments = @($(if ($Release) { 'assembleRelease' } else { 'assembleDebug' }))
if ($Offline) { $arguments = @('--offline') + $arguments }
& $gradle @arguments
if ($LASTEXITCODE -ne 0) { throw "Gradle failed ($LASTEXITCODE)." }

New-Item -ItemType Directory -Force dist | Out-Null
$apk = if ($Release) { 'app\build\outputs\apk\release\app-release.apk' } else { 'app\build\outputs\apk\debug\app-debug.apk' }
Copy-Item $apk dist\pcdiagnostic.apk -Force
Write-Host "Built $(Resolve-Path dist\pcdiagnostic.apk)"
