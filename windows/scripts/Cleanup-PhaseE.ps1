<#
.SYNOPSIS
    Cleanup-PhaseE.ps1 — Removes deprecated Phase E FaceUnlockAuthPackage from LSA registry safely.
.DESCRIPTION
    Backs up LSA registry keys, removes FaceUnlockAuthPackage from Authentication Packages / Security Packages,
    and validates msv1_0 remains intact. Never reboots automatically.
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Phase E Cleanup: FaceUnlock Authentication Package" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# Check Administrator privileges
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script requires Administrator privileges. Please run PowerShell as Administrator."
    exit 1
}

$lsaKeyPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Lsa"
if (-not (Test-Path $lsaKeyPath)) {
    Write-Error "LSA registry key not found at $lsaKeyPath"
    exit 1
}

# 1. Backup LSA Registry Key
$backupDir = "$env:ProgramData\FaceUnlock\backups"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
$backupFile = "$backupDir\Lsa_Backup_PhaseE_$(Get-Date -Format 'yyyyMMdd_HHmmss').reg"
Write-Host "`n[1/3] Backing up LSA registry to: $backupFile" -ForegroundColor Yellow
& reg.exe export "HKLM\SYSTEM\CurrentControlSet\Control\Lsa" "$backupFile" /y | Out-Null
Write-Host "LSA registry backup saved." -ForegroundColor Green

# 2. Inspect and clean 'Authentication Packages'
Write-Host "`n[2/3] Checking 'Authentication Packages'..." -ForegroundColor Yellow
$authPackages = (Get-ItemProperty -Path $lsaKeyPath -Name "Authentication Packages" -ErrorAction SilentlyContinue)."Authentication Packages"
$authList = @()
if ($authPackages) {
    if ($authPackages -is [array]) { $authList = $authPackages } else { $authList = @($authPackages) }
}

$needsAuthUpdate = $false
$newAuthList = @()
foreach ($pkg in $authList) {
    if ($pkg -match "FaceUnlockAuthPackage" -or $pkg -match "FaceUnlock") {
        Write-Host "Found Phase E package in Authentication Packages: $pkg" -ForegroundColor Yellow
        $needsAuthUpdate = $true
    } else {
        $newAuthList += $pkg
    }
}

# Ensure msv1_0 is present
if (-not ($newAuthList -contains "msv1_0")) {
    $newAuthList = @("msv1_0") + $newAuthList
}

if ($needsAuthUpdate) {
    if ($Force -or $PSCmdlet.ShouldProcess("LSA Authentication Packages", "Remove FaceUnlockAuthPackage")) {
        Set-ItemProperty -Path $lsaKeyPath -Name "Authentication Packages" -Value $newAuthList -Type MultiString
        Write-Host "Updated 'Authentication Packages': $($newAuthList -join ', ')" -ForegroundColor Green
    }
} else {
    Write-Host "No FaceUnlock packages found in 'Authentication Packages'. Clean." -ForegroundColor Green
}

# 3. Inspect and clean 'Security Packages'
Write-Host "`n[3/3] Checking 'Security Packages'..." -ForegroundColor Yellow
$secPackages = (Get-ItemProperty -Path $lsaKeyPath -Name "Security Packages" -ErrorAction SilentlyContinue)."Security Packages"
$secList = @()
if ($secPackages) {
    if ($secPackages -is [array]) { $secList = $secPackages } else { $secList = @($secPackages) }
}

$needsSecUpdate = $false
$newSecList = @()
foreach ($pkg in $secList) {
    if ($pkg -match "FaceUnlockAuthPackage" -or $pkg -match "FaceUnlock") {
        Write-Host "Found Phase E package in Security Packages: $pkg" -ForegroundColor Yellow
        $needsSecUpdate = $true
    } else {
        $newSecList += $pkg
    }
}

if ($needsSecUpdate) {
    if ($Force -or $PSCmdlet.ShouldProcess("LSA Security Packages", "Remove FaceUnlockAuthPackage")) {
        Set-ItemProperty -Path $lsaKeyPath -Name "Security Packages" -Value $newSecList -Type MultiString
        Write-Host "Updated 'Security Packages': $($newSecList -join ', ')" -ForegroundColor Green
    }
} else {
    Write-Host "No FaceUnlock packages found in 'Security Packages'. Clean." -ForegroundColor Green
}

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host "  Phase E Cleanup Complete" -ForegroundColor Green
Write-Host "  NO auto-reboot performed. Windows default packages preserved." -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
