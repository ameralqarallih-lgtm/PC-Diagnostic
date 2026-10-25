<#
.SYNOPSIS
    Writes THIRD-PARTY-NOTICES.txt for what the installer ships, and fails on a license the project does not allow.

.DESCRIPTION
    Covers the NuGet packages of the Agent and the Web host (from the restored projects), the npm packages bundled
    into the dashboard (production dependencies in web/package-lock.json, read from web/node_modules), and the
    components shipped outside a package manager (.NET runtime, SQLite, PawnIO, the Inno Setup installer engine).
    A package's own license file is quoted when it has one; otherwise the standard text from installer\licenses is.
    Allowed licenses follow ARCHITECTURE 2a: permissive licenses, MPL-2.0 only for LibreHardwareMonitor and its
    libraries (unmodified), PawnIO's GPL-2.0 only as the separate, unmodified setup program, and LGPL-2.1 only for the
    unmodified PawnIO module files the driver runs.
    Runs on Windows PowerShell 5.1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Output,
    [Parameter(Mandatory = $true)][string] $DotnetVersion,
    [Parameter(Mandatory = $true)][string] $PawnIOVersion,
    [Parameter(Mandatory = $true)][string] $InnoVersion,
    [Parameter(Mandatory = $true)][string] $SeaweedFSVersion,
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$permissive = @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', '0BSD', 'MS-PL', 'Unlicense')
$mplAllowed = @('LibreHardwareMonitorLib', 'BlackSharp.Core', 'DiskInfoToolkit', 'RAMSPDToolkit-NDD')

# Packages whose nuspec gives only a license URL.
$licenseOverrides = @{
    'Mono.Posix.NETStandard' = 'MIT'
}

# Types-only npm packages: nothing of them is in the bundle.
$typesOnly = @('@types/react', 'csstype')

$components = New-Object System.Collections.Generic.List[object]

function Add-Component($name, $version, $license, $url, $copyright, $text, $source) {
    $components.Add([pscustomobject]@{
        Name = $name; Version = $version; License = $license; Url = $url; Copyright = $copyright; Text = $text; Source = $source
    })
}

# ---- NuGet ---------------------------------------------------------------------------------------------------
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$seen = @{}
foreach ($project in @('src\PcDiag.Agent\PcDiag.Agent.csproj', 'src\PcDiag.Web\PcDiag.Web.csproj')) {
    $json = & dotnet list (Join-Path $Root $project) package --include-transitive --format json
    if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed for $project (restore it first)." }
    $list = ($json -join "`n") | ConvertFrom-Json
    foreach ($framework in $list.projects[0].frameworks) {
        $packages = @()
        if ($framework.PSObject.Properties['topLevelPackages']) { $packages += $framework.topLevelPackages }
        if ($framework.PSObject.Properties['transitivePackages']) { $packages += $framework.transitivePackages }
        foreach ($package in $packages) {
            $id = $package.id
            $version = $package.resolvedVersion
            # Native assets for other operating systems are not in the Windows build.
            if ($id -like 'runtime.*' -and $id -notlike 'runtime.win*') { continue }
            if ($seen.ContainsKey("$id/$version")) { continue }
            $seen["$id/$version"] = $true

            $folder = Join-Path $nugetRoot ("{0}\{1}" -f $id.ToLowerInvariant(), $version.ToLowerInvariant())
            $nuspecPath = Get-ChildItem -Path $folder -Filter '*.nuspec' | Select-Object -First 1
            if (-not $nuspecPath) { throw "No nuspec for $id $version in $folder." }
            [xml] $nuspec = Get-Content -Raw -Encoding UTF8 $nuspecPath.FullName
            $metadata = $nuspec.package.metadata
            $license = $null
            $text = $null
            $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
            if ($licenseNode -ne $null) {
                if ($licenseNode.GetAttribute('type') -eq 'expression') {
                    $license = $licenseNode.InnerText.Trim()
                } else {
                    $file = Join-Path $folder $licenseNode.InnerText.Trim()
                    $text = (Get-Content -Raw -Encoding UTF8 $file).Trim()
                    $license = 'see text'
                }
            }

            if ($licenseOverrides.ContainsKey($id)) { $license = $licenseOverrides[$id] }
            if (-not $license) { throw "No license found for NuGet package $id $version; add it to the overrides after checking." }
            $copyright = $metadata.SelectSingleNode("*[local-name()='copyright']")
            $projectUrl = $metadata.SelectSingleNode("*[local-name()='projectUrl']")
            Add-Component $id $version $license ($(if ($projectUrl) { $projectUrl.InnerText } else { "https://www.nuget.org/packages/$id" })) `
                ($(if ($copyright) { $copyright.InnerText } else { $null })) $text 'NuGet'
        }
    }
}

# A license file's text decides its SPDX id when the nuspec only points at the file.
foreach ($component in $components) {
    if ($component.License -eq 'see text') {
        if ($component.Text -match 'Apache License' -and $component.Text -match 'Version 2\.0') { $component.License = 'Apache-2.0' }
        elseif ($component.Text -match 'Permission is hereby granted, free of charge') { $component.License = 'MIT' }
        elseif ($component.Text -match 'Redistribution and use in source and binary forms') { $component.License = 'BSD-3-Clause' }
        else { throw "Cannot tell the license of $($component.Name) from its license file." }
    }
}

# ---- npm (bundled into the dashboard) ----------------------------------------------------------------------
# Windows PowerShell's parser rejects the empty key the lock file uses for the project itself.
$lockText = (Get-Content -Raw -Encoding UTF8 (Join-Path $Root 'web\package-lock.json')) -replace '"packages":\s*\{\s*"":', '"packages": { "(project)":'
$lock = $lockText | ConvertFrom-Json
foreach ($entry in $lock.packages.PSObject.Properties) {
    $path = $entry.Name
    $info = $entry.Value
    if ($path -eq '(project)') { continue }
    if ($info.PSObject.Properties['dev'] -and $info.dev) { continue }
    if ($info.PSObject.Properties['optional'] -and $info.optional) { continue }
    $folder = Join-Path (Join-Path $Root 'web') ($path -replace '/', '\')
    $manifest = Get-Content -Raw -Encoding UTF8 (Join-Path $folder 'package.json') | ConvertFrom-Json
    if ($typesOnly -contains $manifest.name) { continue }
    $license = $manifest.license
    if (-not $license) { throw "No license in $folder\package.json." }
    $licenseFile = Get-ChildItem -Path $folder -File | Where-Object { $_.Name -match '^(LICEN[SC]E|COPYING)(\..*)?$' } | Select-Object -First 1
    $text = if ($licenseFile) { (Get-Content -Raw -Encoding UTF8 $licenseFile.FullName).Trim() } else { $null }
    $url = $null
    if ($manifest.PSObject.Properties['homepage']) { $url = $manifest.homepage }
    elseif ($manifest.PSObject.Properties['repository']) {
        $url = if ($manifest.repository -is [string]) { $manifest.repository } else { $manifest.repository.url }
    }
    if (-not $url) { $url = "https://www.npmjs.com/package/$($manifest.name)" }
    $existing = $components | Where-Object { $_.Name -eq $manifest.name -and $_.Version -eq $manifest.version }
    if ($existing) { continue }
    Add-Component $manifest.name $manifest.version $license $url $null $text 'npm (dashboard)'
}

# ---- Shipped outside a package manager --------------------------------------------------------------------
Add-Component '.NET runtime, ASP.NET Core and Windows Desktop runtime' $DotnetVersion 'MIT' 'https://github.com/dotnet/runtime' `
    '.NET Foundation and Contributors' "Included in the self-contained application folders. Their own third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT and https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt" 'bundled runtime'
Add-Component 'SQLite' '3 (e_sqlite3 from SQLitePCLRaw)' 'Public domain' 'https://www.sqlite.org/copyright.html' $null `
    'The SQLite source code is in the public domain: "Anyone is free to copy, modify, publish, use, compile, sell, or distribute the original SQLite code, either in source code form or as a compiled binary, for any purpose, commercial or non-commercial, and by any means."' 'bundled native library'
Add-Component 'PawnIO (driver installer, PawnIO_setup.exe)' $PawnIOVersion 'GPL-2.0-only' 'https://github.com/namazso/PawnIO (source: https://github.com/namazso/PawnIO and https://github.com/namazso/PawnIO.Setup)' `
    'Copyright namazso' "PawnIO is a separate program, shipped unmodified and installed by its own setup when you choose it during installation (ARCHITECTURE 2a). LibreHardwareMonitor talks to it through its device interface; PC Diagnostic does not link to it. Its complete source code is available at the links above; the GNU General Public License version 2 follows in the license texts." 'separate program'
Add-Component 'PawnIO module Nvidia.bin (agent\pawnio\Nvidia.bin)' '0.2.11 (PawnIO.Modules)' 'LGPL-2.1-or-later' 'https://github.com/namazso/PawnIO.Modules (source: https://github.com/namazso/PawnIO.Modules/blob/0.2.11/Nvidia.p)' `
    'Copyright (C) 2026 CapFrameX, Blacktempel' "A module the PawnIO driver runs to read NVIDIA RTX 50 series thermal registers, shipped unmodified as the signed file from the PawnIO.Modules 0.2.11 release. PC Diagnostic does not link to it; it hands the file to the driver. Its source is at the links above; the GNU Lesser General Public License version 2.1 follows in the license texts." 'PawnIO module'
Add-Component 'SeaweedFS (PC Diagnostic Storage, tools\seaweedfs\weed.exe)' $SeaweedFSVersion 'Apache-2.0' 'https://github.com/seaweedfs/seaweedfs' `
    'Copyright Chris Lu and SeaweedFS contributors' "A separate program, shipped unmodified and run by PC Diagnostic on this PC only (127.0.0.1) as its object storage. The Apache License 2.0 follows in the license texts." 'separate program'
Add-Component 'Inno Setup (installer engine)' $InnoVersion 'Inno Setup License' 'https://jrsoftware.org/isinfo.php' `
    'Copyright (C) 1997-2026 Jordan Russell. Portions Copyright (C) 2000-2026 Martijn Laan.' (Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'licenses\InnoSetup.txt')).Trim() 'installer'

# ---- Policy -----------------------------------------------------------------------------------------------
$problems = @()
foreach ($component in $components) {
    $ids = @($component.License -split '\s+(?:OR|AND)\s+' | ForEach-Object { $_.Trim('(', ')', ' ') })
    $allowed = $false
    foreach ($id in $ids) {
        if ($permissive -contains $id) { $allowed = $true }
        elseif ($id -eq 'MPL-2.0' -and $mplAllowed -contains $component.Name) { $allowed = $true }
        elseif ($id -eq 'GPL-2.0-only' -and $component.Source -eq 'separate program') { $allowed = $true }
        elseif ($id -eq 'LGPL-2.1-or-later' -and $component.Source -eq 'PawnIO module') { $allowed = $true }
        elseif ($id -in @('Public domain', 'Inno Setup License')) { $allowed = $true }
        # Fonts the dashboard bundles: OFL-1.1 allows bundling with any software, only not selling the fonts by themselves.
        elseif ($id -eq 'OFL-1.1' -and $component.Name -like '@fontsource*') { $allowed = $true }
    }
    if ($component.License -match '\bAND\b') {
        $allowed = @($ids | Where-Object { $permissive -notcontains $_ }).Count -eq 0
    }
    if (-not $allowed) { $problems += "$($component.Name) $($component.Version): $($component.License)" }
}
if ($problems.Count -gt 0) {
    throw ("Licenses not allowed by ARCHITECTURE 2a:`n  " + ($problems -join "`n  "))
}

# ---- Output -----------------------------------------------------------------------------------------------
$sorted = $components | Sort-Object Source, Name
$builder = New-Object System.Text.StringBuilder
[void] $builder.AppendLine('PC Diagnostic & Incident Recorder - third-party notices')
[void] $builder.AppendLine('=======================================================')
[void] $builder.AppendLine()
[void] $builder.AppendLine('PC Diagnostic includes or installs the following third-party software. Each is used under the license')
[void] $builder.AppendLine('named below; the full license texts follow at the end. Generated by installer\Generate-Notices.ps1.')
[void] $builder.AppendLine()
foreach ($component in $sorted) {
    [void] $builder.AppendLine(('{0} {1} - {2} ({3})' -f $component.Name, $component.Version, $component.License, $component.Source))
}
[void] $builder.AppendLine()
[void] $builder.AppendLine('-------------------------------------------------------------------------------------------------------')
foreach ($component in $sorted) {
    [void] $builder.AppendLine()
    [void] $builder.AppendLine(('== {0} {1}' -f $component.Name, $component.Version))
    [void] $builder.AppendLine("License: $($component.License)")
    [void] $builder.AppendLine("Project: $($component.Url)")
    if ($component.Copyright) { [void] $builder.AppendLine("Copyright: $($component.Copyright)") }
    if ($component.Text) {
        [void] $builder.AppendLine()
        [void] $builder.AppendLine($component.Text)
    }
}

[void] $builder.AppendLine()
[void] $builder.AppendLine('-------------------------------------------------------------------------------------------------------')
[void] $builder.AppendLine('License texts')
$used = $sorted | ForEach-Object { $_.License -split '\s+(?:OR|AND)\s+' } | ForEach-Object { $_.Trim('(', ')', ' ') } | Sort-Object -Unique
foreach ($id in $used) {
    $file = Join-Path $PSScriptRoot "licenses\$id.txt"
    if (-not (Test-Path $file)) { continue }
    [void] $builder.AppendLine()
    [void] $builder.AppendLine("==== $id")
    [void] $builder.AppendLine()
    [void] $builder.AppendLine((Get-Content -Raw -Encoding UTF8 $file).Trim())
}

$directory = Split-Path -Parent $Output
if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
[System.IO.File]::WriteAllText($Output, $builder.ToString(), (New-Object System.Text.UTF8Encoding($true)))
Write-Host ("Third-party notices: {0} components, licenses {1} -> {2}" -f $components.Count, ($used -join ', '), $Output)
