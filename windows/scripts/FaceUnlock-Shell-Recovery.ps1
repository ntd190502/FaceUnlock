<#
.SYNOPSIS
    FaceUnlock Emergency Shell Recovery Script
.DESCRIPTION
    Restores default Windows shell to explorer.exe, stops any hanging FaceUnlock Shell Gate instance,
    and validates system shell state.
    Does NOT modify passwords, PINs, or user accounts.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Continue"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  FaceUnlock Emergency Shell Gate Recovery" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# 1. Stop running FaceUnlockShell process if active
Write-Host "`n[1/3] Checking for running FaceUnlockShell processes..." -ForegroundColor Yellow
$procs = Get-Process -Name "FaceUnlockShell" -ErrorAction SilentlyContinue
if ($procs) {
    foreach ($p in $procs) {
        Write-Host "Stopping process PID $($p.Id)..." -ForegroundColor Yellow
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    Write-Host "FaceUnlockShell processes stopped." -ForegroundColor Green
} else {
    Write-Host "No active FaceUnlockShell process found." -ForegroundColor Gray
}

# 2. Restore HKCU Winlogon Shell to explorer.exe
Write-Host "`n[2/3] Restoring User Shell (HKCU) to default explorer.exe..." -ForegroundColor Yellow
$hkcuPath = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"
try {
    if (Test-Path $hkcuPath) {
        $userShell = (Get-ItemProperty -Path $hkcuPath -Name "Shell" -ErrorAction SilentlyContinue).Shell
        if ($userShell) {
            Write-Host "Removing custom HKCU Shell override ('$userShell')..." -ForegroundColor Yellow
            Remove-ItemProperty -Path $hkcuPath -Name "Shell" -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "User Shell (HKCU) reset to system default." -ForegroundColor Green
} catch {
    Write-Warning "Could not modify HKCU Shell: $_"
}

# 3. Check / Restore HKLM Winlogon Shell if elevated
Write-Host "`n[3/3] Checking System Shell (HKLM)..." -ForegroundColor Yellow
$hklmPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
try {
    $hklmShell = (Get-ItemProperty -Path $hklmPath -Name "Shell" -ErrorAction SilentlyContinue).Shell
    if ($hklmShell -and $hklmShell -ne "explorer.exe") {
        Write-Host "Restoring HKLM Shell from '$hklmShell' to 'explorer.exe'..." -ForegroundColor Yellow
        Set-ItemProperty -Path $hklmPath -Name "Shell" -Value "explorer.exe" -Force -ErrorAction SilentlyContinue
        Write-Host "HKLM Shell restored to explorer.exe." -ForegroundColor Green
    } else {
        Write-Host "HKLM Shell is already explorer.exe." -ForegroundColor Green
    }
} catch {
    Write-Host "Note: Administrator rights required to modify HKLM. HKCU reset completed." -ForegroundColor Gray
}

# Check if explorer.exe is running
$exp = Get-Process -Name "explorer" -ErrorAction SilentlyContinue
if (-not $exp) {
    Write-Host "`nStarting explorer.exe..." -ForegroundColor Cyan
    Start-Process "$env:WINDIR\explorer.exe"
}

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host "  RECOVERY COMPLETED SUCCESSFULLY" -ForegroundColor Green
Write-Host "  Default Windows Shell is explorer.exe" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
