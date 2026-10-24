<#
.SYNOPSIS
  Runs PC Diagnostic on this PC from the preview build (artifacts\preview): the recorder (elevated, one UAC prompt)
  with its tray icon, one Web host, and the dashboard in the browser.

.EXAMPLE
  .\tools\run-pcdiag.ps1              # start it (or open the dashboard if it is already running)
  .\tools\run-pcdiag.ps1 -Build       # build the current code into the preview first, then start it
  .\tools\run-pcdiag.ps1 -Stop        # stop the recorder and the Web host
#>
param([switch]$Build, [switch]$Stop, [switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$preview = Join-Path $repo 'artifacts\preview'
$agent = Join-Path $preview 'agent\PcDiag.Agent.exe'
$web = Join-Path $preview 'web\PcDiag.Web.exe'
$data = Join-Path $env:LOCALAPPDATA 'PcDiagnostic'

function Stop-All {
    # The recorder is elevated: stopping it needs an elevated PowerShell (one UAC prompt).
    if (Get-Process PcDiag.Agent -ErrorAction SilentlyContinue) {
        Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile -Command "Get-Process PcDiag.Agent -ErrorAction SilentlyContinue | Stop-Process -Force"'
    }
    Get-Process PcDiag.Web -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($Stop) { Stop-All; 'Stopped.'; return }

if ($Build) {
    Push-Location $repo
    try {
        dotnet publish src/PcDiag.Agent -c Release -o artifacts/staging/agent -nologo -v q
        dotnet publish src/PcDiag.Web -c Release -o artifacts/staging/web -nologo -v q
    } finally { Pop-Location }
    Stop-All
    robocopy (Join-Path $repo 'artifacts\staging\agent') (Join-Path $preview 'agent') /MIR /NFL /NDL /NJH /NJS /R:3 /W:1 | Out-Null
    robocopy (Join-Path $repo 'artifacts\staging\web') (Join-Path $preview 'web') /MIR /NFL /NDL /NJH /NJS /R:3 /W:1 | Out-Null
}

if (-not (Test-Path $agent)) { throw "No preview build at $preview. Run with -Build first." }

# The recorder (elevated), which starts its storage host and shows the tray icon.
if (-not (Get-Process PcDiag.Agent -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $agent -Verb RunAs -WorkingDirectory (Split-Path $agent) -ArgumentList "`"--Agent:WebHostPath=$web`""
    while (-not (Get-Process PcDiag.Agent -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
}

# One Web host only (two would take the recorder's connections): the recorder starts its own a few seconds after it
# starts, so it is given 15 s to before one is started here.
for ($i = 0; $i -lt 30 -and -not (Get-Process PcDiag.Web -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
if (-not (Get-Process PcDiag.Web -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $web -ArgumentList '--Web:OpenBrowserOnStart=false' -WorkingDirectory (Split-Path $web) -WindowStyle Hidden
}

# Wait for the Web host to publish its address, then open the dashboard with a one-time sign-in link.
$file = Join-Path $data 'web.json'
for ($i = 0; $i -lt 60; $i++) {
    try { $base = (Get-Content $file -Raw | ConvertFrom-Json).BaseUrl.TrimEnd('/'); Invoke-WebRequest "$base/api/v1/session" -UseBasicParsing -TimeoutSec 2 | Out-Null; break }
    catch [System.Net.WebException] { if ($_.Exception.Response) { break } }
    catch { }
    Start-Sleep -Milliseconds 500
}
"PC Diagnostic is running: $base"
if (-not $NoBrowser) {
    $bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $hash = -join ([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($token)) | ForEach-Object { $_.ToString('x2') })
    New-Item -ItemType Directory -Force (Join-Path $data 'launch') | Out-Null
    [IO.File]::WriteAllText((Join-Path $data "launch\$hash"), [string]([DateTimeOffset]::UtcNow.AddMinutes(2).ToUnixTimeMilliseconds()))
    Start-Process "$base/auth/exchange?t=$token"
}
