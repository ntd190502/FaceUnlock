# Disable-AuthPackage.ps1
# Safely removes FaceUnlockAuthPackage from Windows LSA Authentication Packages list.
# Preserves all built-in packages (msv1_0, kerberos, tspkg, etc.).
# Run as Administrator. Requires system reboot to unload from LSASS.

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
    Write-Host "No Authentication Packages registry key found." -ForegroundColor Yellow
    exit 0
}

$filteredPackages = $currentPackages | Where-Object { $_ -ne $packageName }

if ($filteredPackages.Count -eq $currentPackages.Count) {
    Write-Host "FaceUnlockAuthPackage is not present in LSA Authentication Packages." -ForegroundColor Green
    exit 0
}

# Ensure at least msv1_0 remains
if ($filteredPackages.Count -eq 0) {
    $filteredPackages = @("msv1_0")
}

Set-ItemProperty -Path $lsaKeyPath -Name "Authentication Packages" -Value $filteredPackages -Type MultiString

Write-Host "Successfully removed $packageName from LSA Authentication Packages." -ForegroundColor Green
Write-Host "Remaining packages: $($filteredPackages -join ', ')" -ForegroundColor Yellow
Write-Host "NOTE: A system reboot is required for LSASS to completely unload the package." -ForegroundColor Magenta
