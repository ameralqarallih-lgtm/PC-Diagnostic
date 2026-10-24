<#
.SYNOPSIS
    Builds the PC Diagnostic installer: artifacts\installer\PcDiagnostic-Setup-<version>.exe.

.DESCRIPTION
    1. Fetches the pinned tools into artifacts\tools (checked by SHA-256 and Authenticode signature): Inno Setup
       (unpacked in its portable mode: nothing is installed on this PC) and the PawnIO setup that the installer runs.
    2. Publishes the Agent and the Web host self-contained for win-x64 (the .NET runtime goes inside each folder,
       so the target PC needs nothing else). The Web host build also builds the dashboard (Node.js at build time).
    3. Writes THIRD-PARTY-NOTICES.txt and fails on a license ARCHITECTURE 2a does not allow.
    4. Optionally signs the programs and the installer with a code-signing certificate from your certificate store.
    5. Compiles installer\PcDiagnostic.iss.
    Runs on Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER RuntimePatch
    The .NET runtime patch to bundle (e.g. 10.0.12). Defaults to the latest one in Microsoft's release metadata,
    so the installer carries the current security fixes whatever SDK patch is installed here.

.PARAMETER ReportEndpoint
    A problem-report service (https://..., tools\PcDiag.ReportService), where problem reports are sent:
    reports the user writes, and with the user's consent the errors PC Diagnostic catches by itself. Without it,
    reports are kept on the PC and can be downloaded.

.PARAMETER Version
    The version to build (major.minor.patch). Defaults to major.minor from Directory.Build.props and the number of
    commits as the patch, so every build is numbered higher than the one before.

.PARAMETER CertificateThumbprint
    SHA-1 thumbprint of a code-signing certificate in Cert:\CurrentUser\My (or LocalMachine\My). Without it the
    build is unsigned; Windows SmartScreen then warns when the installer is first run on another PC.

.EXAMPLE
    installer\build.ps1
    installer\build.ps1 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [string] $RuntimePatch,
    [string] $ReportEndpoint,
    [string] $CertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($ReportEndpoint -and $ReportEndpoint -notmatch '^https://[^\s;,"]+/$') { throw '-ReportEndpoint must be an https:// address ending in a slash, without spaces.' }
$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root 'artifacts\tools'
$out = Join-Path $root 'artifacts\installer'
$stage = Join-Path $out 'stage'

# Pinned downloads (update the version, URL and hash together).
$inno = @{
    Version = '7.1.0'
    Url = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe'
    Sha256 = '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F'
}
$pawnIO = @{
    Version = '2.2.0'
    Url = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
    Sha256 = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
}
# PC Diagnostic Storage (SeaweedFS, Apache-2.0): the same release and hashes as LocalObjectStore.cs. Its releases are not
# Authenticode-signed: the archive and weed.exe in it are checked by their pinned SHA-256 instead.
$seaweedFS = @{
    Version = '4.48'
    Url = 'https://github.com/seaweedfs/seaweedfs/releases/download/4.48/windows_amd64.zip'
    Sha256 = 'FE90C04C0620AD1A1C756F86CD5E1443773F56A446688077A4BB5EC04C3CC874'
    ExeSha256 = '394A0154424F3D96F7969C044B77EE4CFB649299F8B42ECC7B703971872CE61D'
}

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Get-Pinned($download, [string] $path) {
    if (-not (Test-Path $path) -or (Get-FileHash -Algorithm SHA256 $path).Hash -ne $download.Sha256) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        Write-Host "Downloading $($download.Url)"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $download.Url -OutFile $path
    }
    $hash = (Get-FileHash -Algorithm SHA256 $path).Hash
    if ($hash -ne $download.Sha256) {
        Remove-Item $path -Force
        throw "SHA-256 of $(Split-Path -Leaf $path) is $hash, expected $($download.Sha256)."
    }
    $signature = Get-AuthenticodeSignature $path
    if ($signature.Status -ne 'Valid') {
        throw "$(Split-Path -Leaf $path) is not validly signed ($($signature.Status))."
    }
    Write-Host ("{0}: SHA-256 ok, signed by {1}" -f (Split-Path -Leaf $path), $signature.SignerCertificate.Subject)
}

# A pinned download that is not signed (SeaweedFS): its SHA-256 only.
function Get-PinnedUnsigned($download, [string] $path) {
    if (-not (Test-Path $path) -or (Get-FileHash -Algorithm SHA256 $path).Hash -ne $download.Sha256) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        Write-Host "Downloading $($download.Url)"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $download.Url -OutFile $path
    }
    $hash = (Get-FileHash -Algorithm SHA256 $path).Hash
    if ($hash -ne $download.Sha256) {
        Remove-Item $path -Force
        throw "SHA-256 of $(Split-Path -Leaf $path) is $hash, expected $($download.Sha256)."
    }
    Write-Host ("{0}: SHA-256 ok (not signed by its makers; pinned)" -f (Split-Path -Leaf $path))
}

function Invoke-Checked([string] $file, [string[]] $arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file exited with $LASTEXITCODE." }
}

# ---- 1. Tools ----------------------------------------------------------------------------------------------
Step "Tools"
$innoDir = Join-Path $tools "innosetup-$($inno.Version)"
$iscc = Join-Path $innoDir 'ISCC.exe'
if (-not (Test-Path $iscc)) {
    $innoSetup = Join-Path $tools "innosetup-$($inno.Version)-x64.exe"
    Get-Pinned $inno $innoSetup
    # Portable mode: the files only, no uninstaller, Start menu entries or file associations.
    $process = Start-Process -FilePath $innoSetup -Wait -PassThru -ArgumentList @(
        '/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/DIR=`"$innoDir`"")
    if ($process.ExitCode -ne 0 -or -not (Test-Path $iscc)) { throw "Unpacking Inno Setup failed (exit code $($process.ExitCode))." }
}
Write-Host "Inno Setup $($inno.Version): $iscc"
$pawnSetup = Join-Path $tools "pawnio-$($pawnIO.Version)\PawnIO_setup.exe"
Get-Pinned $pawnIO $pawnSetup
$seaweedZip = Join-Path $tools "seaweedfs-$($seaweedFS.Version)\windows_amd64.zip"
Get-PinnedUnsigned $seaweedFS $seaweedZip

# ---- 2. Publish ----------------------------------------------------------------------------------------------
if (-not $RuntimePatch) {
    $channel = (Get-Content -Raw (Join-Path $root 'global.json') | ConvertFrom-Json).sdk.version -replace '^(\d+\.\d+)\..*$', '$1'
    $releases = Invoke-RestMethod -UseBasicParsing "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/$channel/releases.json"
    $RuntimePatch = $releases.'latest-runtime'
}
Step "Publishing the Agent and the Web host (self-contained, win-x64, .NET $RuntimePatch)"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
[xml] $props = Get-Content -Raw (Join-Path $root 'Directory.Build.props')
# Every build its own version, always higher than the one before: major.minor from Directory.Build.props, then the
# number of commits (an installer compares it with the version installed: newer upgrades, the same repairs).
$baseVersion = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $Version) {
    $commits = (& git -C $root rev-list --count HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commits -notmatch '^\d+$') { throw 'The build number comes from the git history: run the build in the repository (or pass -Version).' }
    $Version = ($baseVersion -replace '^(\d+\.\d+).*$', '$1') + ".$commits"
    if (& git -C $root status --porcelain --untracked-files=no) {
        Write-Warning "Changes not committed: this build is numbered $Version like the last commit. Commit first for a version of its own."
    }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "-Version must be major.minor.patch (was '$Version')." }
$version = $Version
Write-Host "Version $version"
foreach ($app in @(@{ Project = 'src\PcDiag.Agent\PcDiag.Agent.csproj'; Folder = 'agent' }, @{ Project = 'src\PcDiag.Web\PcDiag.Web.csproj'; Folder = 'web' })) {
    Invoke-Checked 'dotnet' (@('publish', (Join-Path $root $app.Project), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-o', (Join-Path $stage $app.Folder), '-p:DebugType=none', '-p:DebugSymbols=false', "-p:Version=$version", "-p:PcDiagRuntimePatch=$RuntimePatch", '-nologo', '-v', 'q'))
    # XML documentation is for the IDE only.
    Get-ChildItem (Join-Path $stage $app.Folder) -Filter 'PcDiag.*.xml' | Remove-Item -Force
}
# Where problem reports go (Reporting:Endpoint in the Web host's appsettings.json).
if ($ReportEndpoint) {
    $appsettings = Join-Path $stage 'web\appsettings.json'
    $json = Get-Content -Raw $appsettings | ConvertFrom-Json
    $json | Add-Member -NotePropertyName 'Reporting' -NotePropertyValue ([pscustomobject] @{ Endpoint = $ReportEndpoint }) -Force
    $json | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $appsettings
}
Copy-Item $pawnSetup (Join-Path $stage 'PawnIO_setup.exe')
# SeaweedFS's weed.exe, beside the program folders (LocalObjectStore.BundledExe finds it at ..\tools\seaweedfs).
$weedFolder = Join-Path $stage 'tools\seaweedfs'
New-Item -ItemType Directory -Force -Path $weedFolder | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($seaweedZip)
try {
    $entry = $zip.Entries | Where-Object { $_.Name -eq 'weed.exe' } | Select-Object -First 1
    if (-not $entry) { throw "The SeaweedFS archive has no weed.exe." }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $weedFolder 'weed.exe'), $true)
}
finally { $zip.Dispose() }
$weedHash = (Get-FileHash -Algorithm SHA256 (Join-Path $weedFolder 'weed.exe')).Hash
if ($weedHash -ne $seaweedFS.ExeSha256) { throw "SHA-256 of weed.exe is $weedHash, expected $($seaweedFS.ExeSha256)." }
# The character generator (tools\character-gen: CharacterGeneration.FoundOnThisPc finds it at ..\tools\character-gen), its
# files as the repo has them, and the two editor modules pack.mts packs with (lib, beside it). Its Python, packages and
# models are set up from the generator's page.
$genFolder = Join-Path $stage 'tools\character-gen'
if (Test-Path $genFolder) { Remove-Item -Recurse -Force $genFolder }
New-Item -ItemType Directory -Force -Path (Join-Path $genFolder 'lib') | Out-Null
foreach ($file in (& git -C $root ls-files -- 'tools/character-gen')) {
    if ($file -match '\.(py|mts|md)$') { Copy-Item (Join-Path $root $file) (Join-Path $genFolder (Split-Path $file -Leaf)) }
}
foreach ($file in @('pack.ts', 'png8.ts')) { Copy-Item (Join-Path $root "web\src\features\overlay\characters\$file") (Join-Path $genFolder "lib\$file") }
if (-not (Test-Path (Join-Path $genFolder 'serve.py'))) { throw "The character generator was not staged." }
$runtimeConfig = Get-Content -Raw (Join-Path $stage 'agent\PcDiag.Agent.runtimeconfig.json') | ConvertFrom-Json
$dotnetVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version

# ---- 3. Notices --------------------------------------------------------------------------------------------
Step "Third-party notices (.NET $dotnetVersion)"
& (Join-Path $PSScriptRoot 'Generate-Notices.ps1') -Output (Join-Path $stage 'THIRD-PARTY-NOTICES.txt') -DotnetVersion $dotnetVersion `
    -PawnIOVersion $pawnIO.Version -InnoVersion $inno.Version -SeaweedFSVersion $seaweedFS.Version -Root $root

# ---- 4. Signing (optional) ---------------------------------------------------------------------------------
$signArgs = @()
if ($CertificateThumbprint) {
    Step "Signing with certificate $CertificateThumbprint"
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe was not found; install the Windows SDK signing tools.' }
    $ours = Get-ChildItem $stage -Recurse -Include 'PcDiag.*.exe', 'PcDiag.*.dll'
    Invoke-Checked $signtool.FullName (@('sign', '/sha1', $CertificateThumbprint, '/fd', 'sha256', '/tr', $TimestampUrl, '/td', 'sha256') + ($ours | ForEach-Object { $_.FullName }))
    $signArgs = @("/Spcdiag=`"$($signtool.FullName)`" sign /sha1 $CertificateThumbprint /fd sha256 /tr $TimestampUrl /td sha256 `$f", '/DSignTool=pcdiag')
}

# ---- 5. Compile ----------------------------------------------------------------------------------------------
Step "Compiling the installer"
Invoke-Checked $iscc (@('/Qp', "/DAppVersion=$version", "/DStageDir=$stage", "/DOutputDir=$out", "/DPawnIOVersion=$($pawnIO.Version)") + $signArgs + @((Join-Path $PSScriptRoot 'PcDiagnostic.iss')))
$setup = Join-Path $out "PcDiagnostic-Setup-$version.exe"
$hash = (Get-FileHash -Algorithm SHA256 $setup).Hash
Write-Host ("`nInstaller: {0} ({1:N1} MB)`nSHA-256:   {2}" -f $setup, ((Get-Item $setup).Length / 1MB), $hash) -ForegroundColor Green
