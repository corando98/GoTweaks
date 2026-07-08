<#
.SYNOPSIS
    Bundles a built GoTweaks package into the release zip layout.

.DESCRIPTION
    Takes a built AppPackages folder (defaults to the newest one) and
    produces GoTweaks_Release_<version>.zip with this structure:

      GoTweaks_Release_<version>.zip
      ├── XboxGamingBarPackage_<version>.zip     full installer: msixbundle +
      │                                          Dependencies + Install.ps1 +
      │                                          Add-AppDevPackage.ps1 (fresh installs)
      ├── XboxGamingBarPackage_<version>_x64.msixbundle
      │                                          bare bundle (existing users
      │                                          just updating)
      └── Uninstall-GoTweaks.ps1                 clean uninstall + system
                                                 restoration script

.PARAMETER PackageFolder
    Path to a specific built package folder
    (e.g. ...\AppPackages\XboxGamingBarPackage_0.3.2563.0_Test).
    Defaults to the newest folder under XboxGamingBarPackage\AppPackages.

.PARAMETER OutputDir
    Where to write the release zip. Defaults to the repo's 'dist' folder.

.EXAMPLE
    .\Package-Release.ps1
    .\Package-Release.ps1 -PackageFolder ..\XboxGamingBarPackage\AppPackages\XboxGamingBarPackage_0.3.2600.0_Test
#>
[CmdletBinding()]
param(
    [string]$PackageFolder,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $PackageFolder) {
    $appPackages = Join-Path $repoRoot 'XboxGamingBarPackage\AppPackages'
    $PackageFolder = Get-ChildItem $appPackages -Directory |
        Where-Object { $_.Name -match '^XboxGamingBarPackage_[\d.]+_(Debug_)?Test$' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $PackageFolder) { throw "No built package folder found under $appPackages - build first." }
}

if (-not (Test-Path $PackageFolder)) { throw "Package folder not found: $PackageFolder" }

$bundle = Get-ChildItem $PackageFolder -Filter '*.msixbundle' | Select-Object -First 1
if (-not $bundle) { throw "No .msixbundle in $PackageFolder" }

if ($bundle.Name -notmatch '_([\d.]+)_') { throw "Could not parse version from $($bundle.Name)" }
$version = $Matches[1]

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'dist' }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$staging = Join-Path $env:TEMP "GoTweaksRelease_$version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

Write-Host "Packaging GoTweaks $version from $PackageFolder" -ForegroundColor Cyan

# 1. Inner zip: the whole package folder (fresh-install path).
$innerZip = Join-Path $staging "XboxGamingBarPackage_$version.zip"
Write-Host "  creating full-installer zip..."
Compress-Archive -Path (Join-Path $PackageFolder '*') -DestinationPath $innerZip -CompressionLevel Optimal

# 2. Bare msixbundle for updaters.
Write-Host "  copying bare msixbundle for updaters..."
Copy-Item $bundle.FullName -Destination $staging

# 3. Uninstall / restoration script.
$uninstall = Join-Path $PSScriptRoot 'Uninstall-GoTweaks.ps1'
if (Test-Path $uninstall) {
    Copy-Item $uninstall -Destination $staging
} else {
    Write-Warning "Uninstall-GoTweaks.ps1 not found next to this script - release will ship without it"
}

# 4. Outer release zip.
$releaseZip = Join-Path $OutputDir "GoTweaks_Release_$version.zip"
if (Test-Path $releaseZip) { Remove-Item $releaseZip -Force }
Write-Host "  creating $releaseZip..."
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $releaseZip -CompressionLevel Optimal

Remove-Item $staging -Recurse -Force

$size = [math]::Round((Get-Item $releaseZip).Length / 1MB, 1)
Write-Host "Done: $releaseZip ($size MB)" -ForegroundColor Green
Write-Host "Attach this single zip to the GitHub release." -ForegroundColor Green
