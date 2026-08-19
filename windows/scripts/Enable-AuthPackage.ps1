# Enable-AuthPackage.ps1
# Registers FaceUnlockAuthPackage into Windows LSA Authentication Packages list.
# Run as Administrator. Requires system reboot to load into LSASS.

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run as Administrator."
    exit 1
}

$lsaKeyPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Lsa"
$packageName = "FaceUnlockAuthPackage"

Write-Host "Checking current LSA Authentication Packages..." -ForegroundColor Cyan
$currentPackages = (Get-ItemProperty -Path $lsaKeyPath -Name "Authentication Packages" -ErrorAction SilentlyContinue)."Authentication Packages"

if (-not $currentPackages) {
    $currentPackages = @("msv1_0")
}

if ($currentPackages -contains $packageName) {
    Write-Host "FaceUnlockAuthPackage is already registered in LSA Authentication Packages." -ForegroundColor Green
    exit 0
}

$updatedPackages = $currentPackages + $packageName
Set-ItemProperty -Path $lsaKeyPath -Name "Authentication Packages" -Value $updatedPackages -Type MultiString

Write-Host "Successfully added $packageName to LSA Authentication Packages." -ForegroundColor Green
Write-Host "Current packages: $($updatedPackages -join ', ')" -ForegroundColor Yellow
Write-Host "NOTE: A system reboot is required for LSASS to load the new Authentication Package." -ForegroundColor Magenta
