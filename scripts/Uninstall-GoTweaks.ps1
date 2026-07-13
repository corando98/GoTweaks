<#
.SYNOPSIS
    Cleanly uninstalls GoTweaks and restores system state.

.DESCRIPTION
    Runs the deployed helper's --uninstall restoration (stops helper
    processes, clears GoTweaks' HidHide rules so your controller is never
    left hidden, sweeps phantom virtual controllers, removes the scheduled
    task and the deployed helper copy), then removes the GoTweaks app
    package itself.

    Drivers (PawnIO, ViGEmBus, usbip-win2) are LEFT INSTALLED by default
    because other tools share them (RTSS, DS4Windows, Handheld Companion).
    Pass -RemoveDrivers to uninstall them too.

.PARAMETER RemoveDrivers
    Also uninstall PawnIO, ViGEmBus and usbip-win2 via their registered
    uninstallers.

.EXAMPLE
    .\Uninstall-GoTweaks.ps1
    .\Uninstall-GoTweaks.ps1 -RemoveDrivers
#>
[CmdletBinding()]
param(
    [switch]$RemoveDrivers
)

$ErrorActionPreference = 'Continue'

# Self-elevate: if not already Administrator, relaunch this script through UAC
# in a window that stays open. Without this, "Run with PowerShell" opens a
# non-elevated transient window that just closes the instant the admin check
# fails — nothing visible happens.
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "GoTweaks uninstall needs administrator rights. Requesting elevation..." -ForegroundColor Yellow
    $scriptPath = $PSCommandPath
    if (-not $scriptPath) { $scriptPath = $MyInvocation.MyCommand.Definition }
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$scriptPath`"")
    if ($RemoveDrivers) { $argList += '-RemoveDrivers' }
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null
    }
    catch {
        Write-Warning "Elevation was cancelled or failed - cannot uninstall without administrator rights."
        Read-Host "Press Enter to close"
    }
    exit
}

Write-Host "GoTweaks uninstall + system restoration" -ForegroundColor Cyan

# 1. Find the deployed helper (survives even if the store package is already gone).
$package = Get-AppxPackage -Name "PlayandBuildCustom*" -ErrorAction SilentlyContinue | Select-Object -First 1
$helperCandidates = @()
if ($package) {
    $helperCandidates += Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalCache\GoTweaks\Helper\XboxGamingBarHelper.exe"
}
# Fallback: search all package folders for a deployed helper.
$helperCandidates += Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter "PlayandBuildCustom*" -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName "LocalCache\GoTweaks\Helper\XboxGamingBarHelper.exe" }

$helper = $helperCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($helper) {
    Write-Host "Running helper restoration: $helper"
    $helperArgs = @('--uninstall')
    if ($RemoveDrivers) { $helperArgs += '--remove-drivers' }
    $proc = Start-Process -FilePath $helper -ArgumentList $helperArgs -Verb RunAs -PassThru -Wait
    Write-Host "Helper restoration exited with code $($proc.ExitCode)"
} else {
    Write-Warning "Deployed helper not found - doing script-side fallback cleanup only."
    # Scheduled task (helper normally removes this).
    schtasks /Delete /TN "GoTweaks\GoTweaksHelper" /F 2>$null
    # Stop stray helpers.
    Get-Process XboxGamingBarHelper -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Warning "HidHide rules could not be restored automatically (helper missing)."
    Write-Warning "If a controller is missing, open the HidHide Configuration Client and clear the device list."
}

# 2. Remove the app package.
if ($package) {
    Write-Host "Removing app package $($package.PackageFullName)..."
    Remove-AppxPackage -Package $package.PackageFullName
    Write-Host "Package removed." -ForegroundColor Green
} else {
    Write-Host "GoTweaks app package not found (already uninstalled)."
}

# 3. Remove the sideload signing certificate that Add-AppDevPackage installed
# into the machine's Trusted People store. Matched by the package publisher
# (CN=dg) so nothing else is touched.
try {
    $certs = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq 'CN=dg' }
    foreach ($cert in $certs) {
        Write-Host "Removing sideload certificate $($cert.Thumbprint) ($($cert.Subject))..."
        Remove-Item -Path "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)" -Force
    }
    if (-not $certs) { Write-Host "No GoTweaks sideload certificate found (already removed)." }
} catch {
    Write-Warning "Certificate removal failed: $_ (remove manually via certlm.msc, Trusted People, CN=dg)"
}

Write-Host "Done. A reboot is recommended if controllers were hidden or drivers removed." -ForegroundColor Cyan

# Keep the elevated window open so the user can read the result instead of it
# vanishing the moment the script ends.
Read-Host "Press Enter to close"
